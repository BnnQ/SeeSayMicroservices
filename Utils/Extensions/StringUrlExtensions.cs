using System.Linq;

namespace SeeSayMicroservices.Utils.Extensions;

public static class StringUrlExtensions
{
    public static string ToNgrokUrl(this string url, string ngrokUrl)
    {
        if (url.Count(symbol => symbol == '/') < 3)
            return ngrokUrl;
        
        var firstSlashIndex = url.IndexOf('/');
        var thirdSlashIndex = url.IndexOf('/', firstSlashIndex + 2);

        var resultUrl = ngrokUrl + url[thirdSlashIndex..];
        return resultUrl;
    }
    
}