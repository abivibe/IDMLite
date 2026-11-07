import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';
import 'cobalt_resolver.dart';

class TikTokResolver {
  static bool canResolve(String url) {
    final lower = url.toLowerCase();
    return lower.contains('tiktok.com/');
  }

  static Future<MediaProbeResult> resolve(String url) async {
    try {
      final resp = await http.post(
        Uri.parse('https://www.tikwm.com/api/'),
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
          'User-Agent':
              'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
        },
        body: {'url': url, 'hd': '1'},
      ).timeout(const Duration(seconds: 8));

      if (resp.statusCode == 200 && resp.body.isNotEmpty) {
        final json = jsonDecode(resp.body);
        if (json is Map && json['data'] is Map) {
          final data = json['data'] as Map;
          String? directUrl = (data['play'] ?? data['wmplay']) as String?;
          if (directUrl != null && directUrl.isNotEmpty) {
            if (!directUrl.startsWith('http')) {
              directUrl = 'https://www.tikwm.com$directUrl';
            }

            final title = (data['title'] as String?) ?? 'TikTok_Video';
            final cleanTitle = title.replaceAll(RegExp(r'[\r\n\t]+'), ' ').trim();
            final shortTitle = cleanTitle.length > 50 ? cleanTitle.substring(0, 50) : cleanTitle;
            final safeTitle = shortTitle.replaceAll(RegExp(r'[\\/:*?"<>|]'), '_').trim();

            int? size = await CobaltResolver.probeUrlSize(directUrl);

            return MediaProbeResult(
              success: true,
              originalUrl: url,
              title: safeTitle.isNotEmpty ? safeTitle : 'TikTok_Video',
              directUrl: directUrl,
              suggestedFileName: '${safeTitle.isNotEmpty ? safeTitle : "TikTok_Video"}.mp4',
              fileSize: size,
              quality: 'HD No Watermark',
              format: 'mp4',
              provider: 'tikwm',
              streams: [
                MediaStreamOption(
                  streamType: 'video',
                  title: '$safeTitle (MP4 Video)',
                  format: 'mp4',
                  quality: 'HD No Watermark',
                  url: directUrl,
                  suggestedFileName: '$safeTitle.mp4',
                  fileSize: size,
                  isAudioOnly: false,
                ),
              ],
            );
          }
        }
      }
    } catch (_) {}

    return MediaProbeResult.failure(url, 'TikTok videosu çözülemedi', provider: 'tikwm');
  }
}
