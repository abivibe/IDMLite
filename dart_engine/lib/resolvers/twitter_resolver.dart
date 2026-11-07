import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';
import 'cobalt_resolver.dart';

class TwitterResolver {
  static bool canResolve(String url) {
    final lower = url.toLowerCase();
    return (lower.contains('twitter.com/') || lower.contains('x.com/')) &&
        lower.contains('/status/');
  }

  static Future<MediaProbeResult> resolve(String url) async {
    final match = RegExp(
      r'(?:twitter\.com|x\.com)\/(?:([a-zA-Z0-9_]+)\/)?status\/(\d+)',
      caseSensitive: false,
    ).firstMatch(url);

    if (match == null) {
      return MediaProbeResult.failure(url, 'Geçersiz X/Twitter URL deseni', provider: 'fxtwitter');
    }

    final user = match.group(1);
    final tweetId = match.group(2)!;

    final endpoints = <String>[];
    if (user != null && user != 'i') {
      endpoints.add('https://api.fxtwitter.com/$user/status/$tweetId');
    }
    endpoints.add('https://api.fxtwitter.com/i/status/$tweetId');
    if (user != null && user != 'i') {
      endpoints.add('https://api.vxtwitter.com/$user/status/$tweetId');
    }
    endpoints.add('https://api.vxtwitter.com/i/status/$tweetId');

    for (final ep in endpoints) {
      try {
        final resp = await http.get(
          Uri.parse(ep),
          headers: {
            'Accept': 'application/json',
            'User-Agent':
                'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
          },
        ).timeout(const Duration(seconds: 6));

        if (resp.statusCode == 200 && resp.body.isNotEmpty) {
          final data = jsonDecode(resp.body);
          if (data is Map && data['tweet'] != null) {
            final tweet = data['tweet'] as Map;

            String title = 'X_Video_$tweetId';
            final text = tweet['text'] as String?;
            final authorMap = tweet['author'] as Map?;
            final authorName = authorMap != null ? (authorMap['name'] as String?) : null;

            if (text != null && text.trim().isNotEmpty) {
              final cleanText = text.trim().replaceAll(RegExp(r'[\r\n\t]+'), ' ');
              final shortText = cleanText.length > 50 ? cleanText.substring(0, 50) : cleanText;
              title = (authorName != null && authorName.isNotEmpty)
                  ? '$authorName - $shortText'
                  : shortText;
            }

            String? videoUrl;

            // 1. media.videos listesi
            if (tweet['media'] is Map && tweet['media']['videos'] is List) {
              final videos = tweet['media']['videos'] as List;
              if (videos.isNotEmpty && videos.first is Map && videos.first['url'] != null) {
                videoUrl = videos.first['url'] as String?;
              }
            }

            // 2. media_extended listesi
            if (videoUrl == null && tweet['media_extended'] is List) {
              for (final item in tweet['media_extended'] as List) {
                if (item is Map && item['type'] == 'video' && item['url'] != null) {
                  videoUrl = item['url'] as String?;
                  break;
                }
              }
            }

            // 3. media.all listesi
            if (videoUrl == null && tweet['media'] is Map && tweet['media']['all'] is List) {
              for (final item in tweet['media']['all'] as List) {
                if (item is Map && item['type'] == 'video' && item['url'] != null) {
                  videoUrl = item['url'] as String?;
                  break;
                }
              }
            }

            // 4. video.url tekil alan
            if (videoUrl == null && tweet['video'] is Map && tweet['video']['url'] != null) {
              videoUrl = tweet['video']['url'] as String?;
            }

            if (videoUrl != null && videoUrl.isNotEmpty) {
              int? size = await CobaltResolver.probeUrlSize(videoUrl);
              final safeTitle = title.replaceAll(RegExp(r'[\\/:*?"<>|]'), '_').trim();

              return MediaProbeResult(
                success: true,
                originalUrl: url,
                title: safeTitle,
                directUrl: videoUrl,
                suggestedFileName: '$safeTitle.mp4',
                fileSize: size,
                quality: 'HD Video',
                format: 'mp4',
                provider: 'fxtwitter',
                streams: [
                  MediaStreamOption(
                    streamType: 'video',
                    title: '$safeTitle (MP4 Video)',
                    format: 'mp4',
                    quality: 'HD Video',
                    url: videoUrl,
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
    }

    return MediaProbeResult.failure(url, 'X/Twitter videosu çözülemedi', provider: 'fxtwitter');
  }
}
