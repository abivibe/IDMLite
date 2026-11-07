namespace IDMLite.Core.Services;

public static class YtDlpFailure
{
    public static string Classify(string? output)
    {
        bool Has(params string[] terms) => terms.Any(term => output?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
        if (Has("unsupported url")) return "unsupported";
        if (Has("private video", "this video is private", "private account")) return "private";
        if (Has("age-restricted", "confirm your age", "age restriction")) return "age";
        if (Has("not available in your country", "geo restricted", "geo-restricted", "geographic restriction")) return "region";
        if (Has("http error 429", "too many requests", "rate limit")) return "rate_limit";
        if (Has("login required", "sign in", "log in", "authentication required")) return "login";
        if (Has("certificate_verify_failed", "ssl", "tls")) return "tls";
        if (Has("timed out", "timeout", "connection reset", "connection refused", "http error 5")) return "network";
        if (Has("expired", "http error 410")) return "expired";
        if (Has("http error 403", "forbidden")) return "forbidden";
        if (Has("ffmpeg") && Has("not found", "not installed")) return "ffmpeg_missing";
        if (Has("requested format is not available", "no video formats found")) return "format";
        if (Has("video unavailable", "not available", "has been removed", "http error 404")) return "unavailable";
        if (Has("outdated", "update yt-dlp")) return "outdated";
        return "extractor";
    }

    public static string Message(string category) => category switch
    {
        "unsupported" => "Bu bağlantı yt-dlp tarafından desteklenmiyor.",
        "private" => "Video özel veya hesabınızın erişim izni yok.",
        "age" => "Video yaş doğrulaması gerektiriyor.",
        "region" => "Video bulunduğunuz bölgede sunulmuyor.",
        "rate_limit" => "Platform istek sınırına ulaştı. Daha sonra tekrar deneyin.",
        "login" => "Video için geçerli bir tarayıcı oturumu gerekiyor.",
        "tls" => "Platformla güvenli bağlantı kurulamadı.",
        "network" => "Platforma bağlanırken ağ hatası oluştu.",
        "expired" => "Medya bağlantısının süresi doldu. Videonun sayfasından yeniden deneyin.",
        "forbidden" => "Platform medya erişimini reddetti (HTTP 403).",
        "ffmpeg_missing" => "Videoyu hazırlamak için FFmpeg gerekiyor.",
        "format" => "İstenen medya biçimi platformda bulunamadı.",
        "unavailable" => "Video kaldırılmış veya şu anda kullanılamıyor.",
        "outdated" => "yt-dlp bu platform için güncelleme gerektiriyor.",
        _ => "yt-dlp medya bilgilerini çıkaramadı."
    };
}
