using System.Text.Encodings.Web;

namespace DotNetLab;

public static class NetUtil
{
    public static Uri WithCorsProxy(this Uri uri)
    {
        return uri.ToString().WithCorsProxy();
    }

    public static Uri WithCorsProxy(this string uri)
    {
        return new Uri("https://cloudflare-cors-anywhere.knowpicker.workers.dev/?" +
            UrlEncoder.Default.Encode(uri));
    }
}
