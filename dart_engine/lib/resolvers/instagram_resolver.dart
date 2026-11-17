import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';
import 'media_size_probe.dart';

class InstagramResolver {
  static bool canResolve(String url) {
    final lower = url.toLowerCase();
    return lower.contains('instagram.com/p/') ||
        lower.contains('instagram.com/reel/') ||
        lower.contains('instagram.com/reels/') ||
        lower.contains('instagram.com/tv/') ||
        lower.contains('instagram.com/share/') ||
        lower.contains('cdninstagram.com') ||
        lower.contains('fbcdn.net');
  }

  static Future<MediaProbeResult> resolve(String url) async {
    if (url.contains('cdninstagram.com') || url.contains('fbcdn.net')) {
      int? size = await MediaSizeProbe.probeUrlSize(url);
      final safeTitle = 'Instagram_Video_${DateTime.now().millisecondsSinceEpoch}';
      return MediaProbeResult(
        success: true,
        originalUrl: url,
        title: safeTitle,
        directUrl: url,
        suggestedFileName: '$safeTitle.mp4',
        fileSize: size,
        quality: 'Reels HD',
        format: 'mp4',
        provider: 'cdn',
        streams: [
          MediaStreamOption(
            streamType: 'video',
            title: '$safeTitle (Reels MP4)',
            format: 'mp4',
            quality: 'Reels HD',
            url: url,
            suggestedFileName: '$safeTitle.mp4',
            fileSize: size,
            isAudioOnly: false,
          ),
        ],
      );
    }

    final match = RegExp(
      r'(?:p|reel|reels|tv|share\/reel|share\/p|reels\/videos)\/([a-zA-Z0-9_-]{7,25})',
      caseSensitive: false,
    ).firstMatch(url) ?? RegExp(
      r'\/(?:reel|p)\/([a-zA-Z0-9_-]{7,25})',
      caseSensitive: false,
    ).firstMatch(url);

    final shortcode = match?.group(1);
    String? directUrl;
    String title = 'Instagram_Video_${DateTime.now().millisecondsSinceEpoch}';

    // 2. Embed Captioned Sayfası (Başlık ve alternatif video akışı)
    try {
      final endpoints = <String>[];
      if (shortcode != null && shortcode.isNotEmpty) {
        endpoints.add('https://www.instagram.com/reel/$shortcode/embed/captioned/');
        endpoints.add('https://www.instagram.com/p/$shortcode/embed/captioned/');
      } else {
        endpoints.add(url);
      }

      for (final ep in endpoints) {
        final resp = await http.get(
          Uri.parse(ep),
          headers: {
            'User-Agent':
                'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36',
            'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
          },
        ).timeout(const Duration(seconds: 8));

        if (resp.statusCode == 200 && resp.body.isNotEmpty) {
          final html = resp.body;

          if (directUrl == null) {
            final vm = RegExp(r'(?:video_url|videoUrl|contentUrl)\\":\\"([^"\\]+)\\"|"video_url"\s*:\s*"([^"]+)"')
                .firstMatch(html);
            if (vm != null) {
              final raw = (vm.group(1) ?? vm.group(2))!;
              directUrl = raw.replaceAll(r'\u0026', '&').replaceAll(r'\/', '/');
            }
          }

          // Author
          String author = '';
          final authorMatch = RegExp(r'class=["'']CaptionUsername["''][^>]*><a[^>]*>([^<]+)<\/a>').firstMatch(html);
          if (authorMatch != null) author = authorMatch.group(1)?.trim() ?? '';

          // Caption
          String caption = '';
          final capMatch = RegExp(r'class=["'']Caption["''][^>]*>([\s\S]*?)<\/div>').firstMatch(html);
          if (capMatch != null) {
            caption = capMatch.group(1)?.replaceAll(RegExp(r'<[^>]+>'), ' ').trim() ?? '';
          }

          if (caption.isNotEmpty) {
            final cleanCap = caption.replaceAll(RegExp(r'[\r\n\t]+'), ' ');
            final shortCap = cleanCap.length > 60 ? cleanCap.substring(0, 60) : cleanCap;
            title = author.isNotEmpty ? '$author - $shortCap' : shortCap;
            break;
          } else if (author.isNotEmpty) {
            title = '$author - Instagram Video';
            break;
          }
        }
      }
    } catch (_) {}

    if (directUrl != null && directUrl.isNotEmpty) {
      int? size = await MediaSizeProbe.probeUrlSize(directUrl);
      final safeTitle = title.replaceAll(RegExp(r'[\\/:*?"<>|]'), '_').trim();

      return MediaProbeResult(
        success: true,
        originalUrl: url,
        title: safeTitle,
        directUrl: directUrl,
        suggestedFileName: '$safeTitle.mp4',
        fileSize: size,
        quality: 'Reels HD',
        format: 'mp4',
        provider: 'instagram_embed',
        streams: [
          MediaStreamOption(
            streamType: 'video',
            title: '$safeTitle (Reels MP4)',
            format: 'mp4',
            quality: 'Reels HD',
            url: directUrl,
            suggestedFileName: '$safeTitle.mp4',
            fileSize: size,
            isAudioOnly: false,
          ),
        ],
      );
    }

    return MediaProbeResult.failure(url, 'Instagram videosu doğrudan çözülemedi', provider: 'instagram');
  }
}
