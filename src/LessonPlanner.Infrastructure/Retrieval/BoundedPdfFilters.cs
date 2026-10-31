using System.Buffers;
using System.IO.Compression;
using LessonPlanner.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace LessonPlanner.Infrastructure.Retrieval;

internal sealed class BoundedPdfFilters(PdfExtractionBudget budget) : IFilterProvider
{
    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary)
    {
        budget.Check();
        if ((dictionary.TryGet(NameToken.Filter, out var token) || dictionary.TryGet(NameToken.F, out token)) &&
            token is ArrayToken array && array.Data.Count > 4)
            throw new DomainException("source_pdf_limit", "The PDF uses too many chained stream filters.");
        return Wrap(DefaultFilterProvider.Instance.GetFilters(dictionary));
    }

    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names)
    {
        budget.Check();
        if (names.Count > 4) throw new DomainException("source_pdf_limit", "The PDF uses too many chained stream filters.");
        return Wrap(DefaultFilterProvider.Instance.GetNamedFilters(names));
    }

    public IReadOnlyList<IFilter> GetAllFilters() => Wrap(DefaultFilterProvider.Instance.GetAllFilters());

    private IFilter[] Wrap(IReadOnlyList<IFilter> filters) =>
        filters.Select(filter => (IFilter)new BoundedFilter(filter, budget)).ToArray();

    private sealed class BoundedFilter(IFilter inner, PdfExtractionBudget budget) : IFilter
    {
        public bool IsSupported => inner is FlateFilter or Ascii85Filter or AsciiHexDecodeFilter;

        public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
        {
            budget.Check();
            if (!IsSupported)
                throw new DomainException("source_invalid_pdf", "The PDF uses a stream encoding outside the supported curriculum profile.");
            if (input.Length > PdfExtractionBudget.MaximumDecodedStreamBytes)
                throw new DomainException("source_pdf_limit", "A PDF stream exceeds the extraction budget.");

            if (inner is FlateFilter)
            {
                ValidateInflation(input, streamDictionary, filterIndex);
            }
            else
            {
                // ASCII85's 'z' shorthand expands one input byte into four output bytes.
                var upperBound = inner is Ascii85Filter ? (long)input.Length * 4 : input.Length;
                if (upperBound > PdfExtractionBudget.MaximumDecodedStreamBytes)
                    throw new DomainException("source_pdf_limit", "A PDF stream exceeds the extraction budget.");
                budget.ChargeDecoded(upperBound);
            }

            var decoded = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
            budget.Check();
            if (decoded.Length > PdfExtractionBudget.MaximumDecodedStreamBytes)
                throw new DomainException("source_pdf_limit", "A decoded PDF stream exceeds the extraction budget.");
            return decoded;
        }

        private void ValidateInflation(Memory<byte> input, DictionaryToken dictionary, int filterIndex)
        {
            var parameters = DecodeParameterResolver.GetFilterParameters(dictionary, filterIndex);
            var predictor = Parameter(parameters, NameToken.Predictor, 1, 1, 15);
            var colors = Parameter(parameters, NameToken.Colors, 1, 1, 4);
            var bits = Parameter(parameters, NameToken.BitsPerComponent, 8, 1, 16);
            var columns = Parameter(parameters, NameToken.Columns, 1, 1, 8192);
            if (predictor is not (1 or 2 or >= 10 and <= 15) || bits is not (1 or 2 or 4 or 8 or 16))
                throw new DomainException("source_invalid_pdf", "The PDF uses unsupported predictor parameters.");

            // Probe inflation with bounded buffers before PdfPig allocates its decoded stream.
            using var compressed = new MemoryStream(input.ToArray(), writable: false);
            using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            long decodedLength = 0;
            try
            {
                while (true)
                {
                    budget.Check();
                    var read = inflater.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    decodedLength += read;
                    if (decodedLength > PdfExtractionBudget.MaximumDecodedStreamBytes)
                        throw new DomainException("source_pdf_limit", "A compressed PDF stream exceeds the inflation budget.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var rowLength = (colors * bits * columns + 7) / 8;
            var encodedRowLength = rowLength + (predictor >= 10 ? 1 : 0);
            if (predictor > 1 && decodedLength % encodedRowLength != 0)
                throw new DomainException("source_invalid_pdf", "The PDF contains an incomplete predictor row.");
            budget.ChargeDecoded(decodedLength);
        }

        private static int Parameter(DictionaryToken parameters, NameToken name, int fallback, int minimum, int maximum)
        {
            if (!parameters.TryGet(name, out var token)) return fallback;
            if (token is not NumericToken number || !double.IsFinite(number.Data) ||
                number.Data != Math.Truncate(number.Data) || number.Data < minimum || number.Data > maximum)
                throw new DomainException("source_invalid_pdf", "The PDF contains invalid stream parameters.");
            return (int)number.Data;
        }
    }
}
