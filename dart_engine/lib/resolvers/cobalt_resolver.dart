import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';

class CobaltResolver {
  static final List<String> cobaltInstances = [
    'https://rue-cobalt.xenon.zone',
    'https://cobaltapi.cjs.nz',
    'https://cobaltapi.kittycat.boo',
    'https://cobaltapi.squair.xyz',
    'https://dog.kittycat.boo',
    'https://api.cobalt.rpkiinval.id',
    'https://melon.clxxped.lol',
    'https://grapefruit.clxxped.lol',
    'https://lime.clxxped.lol',
  ];

  static bool canResolve(String url) {
    final lower = url.toLowerCase();
    return lower.contains('instagram.com/') ||
        lower.contains('twitter.com/') ||
        lower.contains('x.com/') ||
        lower.contains('tiktok.com/') ||
        lower.contains('youtube.com/') ||
        lower.contains('youtu.be/');
  }

  static Future<int?> probeUrlSize(String streamUrl) async {
    try {
      final uri = Uri.parse(streamUrl);
      final headResp = await http.head(uri).timeout(const Duration(seconds: 4));
      if (headResp.statusCode >= 200 && headResp.statusCode < 400) {
        final clen = headResp.headers['content-length'];
        if (clen != null && int.tryParse(clen) != null && int.parse(clen) > 0) {
          return int.parse(clen);
        }
      }

      // Fallback: GET with Range: bytes=0-0
      final getResp = await http.get(
        uri,
        headers: {'Range': 'bytes=0-0'},
      ).timeout(const Duration(seconds: 4));

      if (getResp.statusCode == 206) {
        final crange = getResp.headers['content-range']; // e.g. bytes 0-0/123456
        if (crange != null) {
          final match = RegExp(r'/(\d+)').firstMatch(crange);
          if (match != null && match.group(1) != null) {
            return int.tryParse(match.group(1)!);
          }
        }
      } else if (getResp.statusCode == 200) {
        final clen = getResp.headers['content-length'];
        if (clen != null && int.tryParse(clen) != null && int.parse(clen) > 0) {
          return int.parse(clen);
        }
      }
    } catch (_) {}
    return null;
  }

  static Future<MediaProbeResult> resolve(String url) async {
    final payload = jsonEncode({
      'url': url,
      'videoQuality': 'max',
      'downloadMode': 'auto',
    });

    for (final instance in cobaltInstances) {
      try {
        final endpoint = Uri.parse(instance.endsWith('/') ? '${instance}api/json' : '$instance/');
        final resp = await http.post(
          endpoint,
          headers: {
            'Accept': 'application/json',
            'Content-Type': 'application/json',
            'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0',
          },
          body: payload,
        ).timeout(const Duration(seconds: 6));

        if (resp.statusCode >= 200 && resp.statusCode < 300) {
          final data = jsonDecode(resp.body) as Map<String, dynamic>;
          final status = data['status'] as String? ?? '';

          String? directUrl;
          String? directAudioUrl;
          String title = data['filename'] as String? ?? 'Social_Video';

          if (status == 'tunnel' || status == 'redirect' || status == 'stream') {
            directUrl = data['url'] as String?;
          } else if (status == 'picker') {
            final picker = data['picker'] as List<dynamic>? ?? [];
            for (final item in picker) {
              if (item is Map<String, dynamic>) {
                final type = item['type'] as String? ?? 'video';
                final itemUrl = item['url'] as String?;
                if (type == 'video' && directUrl == null) {
                  directUrl = itemUrl;
                } else if (type == 'audio' && directAudioUrl == null) {
                  directAudioUrl = itemUrl;
                }
              }
            }
          }

          if (directUrl != null && directUrl.isNotEmpty) {
            // Guarantee .mp4 extension (solves X/Twitter .ts issue!)
            if (!title.toLowerCase().endsWith('.mp4')) {
              title = title.replaceAll(RegExp(r'\.[a-zA-Z0-9]+$'), '');
              title = '$title.mp4';
            }

            // Immediately probe exact byte size in parallel
            final exactSize = await probeUrlSize(directUrl);

            final streams = <MediaStreamOption>[
              MediaStreamOption(
                streamType: 'video',
                title: '$title (Direct MP4 HD)',
                format: 'mp4',
                quality: '1080p HD',
                url: directUrl,
                suggestedFileName: title,
                fileSize: exactSize,
                isAudioOnly: false,
              ),
            ];

            if (directAudioUrl != null && directAudioUrl.isNotEmpty) {
              final audioSize = await probeUrlSize(directAudioUrl);
              final audioTitle = title.replaceAll(RegExp(r'\.mp4$', caseSensitive: false), '.mp3');
              streams.add(MediaStreamOption(
                streamType: 'audio',
                title: '$audioTitle (HQ Audio)',
                format: 'mp3',
                quality: 'HQ Audio',
                url: directAudioUrl,
                suggestedFileName: audioTitle,
                fileSize: audioSize,
                isAudioOnly: true,
              ));
            }

            return MediaProbeResult(
              success: true,
              originalUrl: url,
              title: title.replaceAll(RegExp(r'\.mp4$', caseSensitive: false), ''),
              directUrl: directUrl,
              directAudioUrl: directAudioUrl,
              suggestedFileName: title,
              fileSize: exactSize,
              quality: '1080p HD',
              format: 'mp4',
              provider: 'cobalt',
              streams: streams,
            );
          }
        }
      } catch (_) {
        // Try next cobalt instance in fallback chain
        continue;
      }
    }

    return MediaProbeResult.failure(url, 'All Cobalt instances failed to resolve link', provider: 'cobalt');
  }
}
