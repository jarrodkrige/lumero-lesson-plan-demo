using System.Net;
using System.Text;
using LessonPlanner.Core;
using LessonPlanner.Infrastructure.Retrieval;
using QRCoder;

namespace LessonPlanner.Infrastructure.Rendering;

public sealed class LocalQuizQrRenderer(bool allowHttpLocalhost = false) : IQuizQrRenderer
{
    public byte[] Render(Uri publicUrl)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        RequireCanonicalQuizUrl(publicUrl);
        using var data = QRCodeGenerator.GenerateQrCode(publicUrl.AbsoluteUri, QRCodeGenerator.ECCLevel.Q,
            forceUtf8: true, utf8BOM: false);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule: 8, drawQuietZones: true);
    }

    private void RequireCanonicalQuizUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || !url.IsWellFormedOriginalString() ||
            !string.Equals(url.OriginalString, url.AbsoluteUri, StringComparison.Ordinal) ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            url.AbsolutePath.Length <= 1 || Encoding.UTF8.GetByteCount(url.AbsoluteUri) > 1024)
            throw InvalidUrl();

        var host = url.DnsSafeHost;
        IPAddress.TryParse(host, out var address);
        var isLocalhost = string.Equals(host, "localhost", StringComparison.Ordinal) ||
            (address is not null && (address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback)));
        if (isLocalhost)
        {
            if (!allowHttpLocalhost || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                throw InvalidUrl();
            return;
        }

        if (url.Scheme != Uri.UriSchemeHttps || (address is not null && !PublicAddressPolicy.IsPublic(address)) ||
            (address is null && (url.HostNameType != UriHostNameType.Dns || !host.Contains('.') ||
                host.EndsWith('.') || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))))
            throw InvalidUrl();
    }

    private static DomainException InvalidUrl() => new("quiz_url_invalid", "QR rendering requires an already canonical public HTTPS quiz URL; localhost is opt-in for local verification only.");
}
