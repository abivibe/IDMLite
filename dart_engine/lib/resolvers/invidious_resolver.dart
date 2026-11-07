import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';
import 'cobalt_resolver.dart';

class InvidiousResolver {
  static final List<String> invidiousInstances = [
    'https://inv.nadeko.net',
    'https://invidious.nerdvpn.de',
    'https://invidious.privacydev.net',
    'https://yewtu.be',
    'https://invidious.jing.rocks',
    'https://invidious.f5.si',
  ];

  static String? extractVideoId(String url) {
    final match = RegExp(r'(?:v=|\/|youtu\.be\/|shorts\/)([a-zA-Z0-9_-]{11})').firstMatch(url);
    return match?.group(1);
  }

  static bool canResolve(String url) {
    return extractVideoId(url) != null;
  }

  static Future<MediaProbeResult> resolve(String url) async {
    final videoId = extractVideoId(url);
    if (videoId == null) {
      return MediaProbeResult.failure(url, 'Invalid YouTube video ID', provider: 'invidious');
    }

    for (final instance in invidiousInstances) {
      try {
        final uri = Uri.parse('$instance/api/v1/videos/$videoId');
        final resp = await http.get(
          uri,
          headers: {'Accept': 'application/json', 'User-Agent': 'IDMLite/1.0'},
        ).timeout(const Duration(seconds: 4));

        if (resp.statusCode == 200) {
          final data = jsonDecode(resp.body) as Map<String, dynamic>;
          final rawTitle = data['title'] as String? ?? 'YouTube_Video';
          final title = rawTitle.replaceAll(RegExp(r'[/\\?%*:|"<>]+'), '_').trim();

          final formatStreams = data['formatStreams'] as List<dynamic>? ?? [];
          final adaptiveFormats = data['adaptiveFormats'] as List<dynamic>? ?? [];

          String? directVideoUrl;
          String? directAudioUrl;
          int? directVideoSize;
          int? directAudioSize;
          String quality = '720p HD';

          // Try formatStreams first (muxed single-piece)
          if (formatStreams.isNotEmpty) {
            final first = formatStreams.last as Map<String, dynamic>;
            directVideoUrl = first['url'] as String?;
            quality = first['qualityLabel'] as String? ?? '720p';
            final sizeStr = first['size'] as String?;
            if (sizeStr != null && int.tryParse(sizeStr) != null) {
              directVideoSize = int.parse(sizeStr);
            }
          }

          // If no size, check clen query param or adaptiveFormats
          if (directVideoUrl != null && (directVideoSize == null || directVideoSize == 0)) {
            final clenMatch = RegExp(r'[?&](?:clen|size)=(\d+)').firstMatch(directVideoUrl);
            if (clenMatch != null && clenMatch.group(1) != null) {
              directVideoSize = int.tryParse(clenMatch.group(1)!);
            }
          }

          // Check audio from adaptiveFormats
          for (final fmt in adaptiveFormats) {
            if (fmt is Map<String, dynamic>) {
              final type = fmt['type'] as String? ?? '';
              if (type.contains('audio') && directAudioUrl == null) {
                directAudioUrl = fmt['url'] as String?;
                final clenMatch = RegExp(r'[?&](?:clen|size)=(\d+)').firstMatch(directAudioUrl ?? '');
                if (clenMatch != null && clenMatch.group(1) != null) {
                  directAudioSize = int.tryParse(clenMatch.group(1)!);
                }
              }
            }
          }

          // Parallel probe if size still unknown
          if (directVideoUrl != null && directVideoSize == null) {
            directVideoSize = await CobaltResolver.probeUrlSize(directVideoUrl);
          }

          if (directVideoUrl != null) {
            final streams = <MediaStreamOption>[
              MediaStreamOption(
                streamType: 'video',
                title: '$title ($quality MP4)',
                format: 'mp4',
                quality: quality,
                url: directVideoUrl,
                suggestedFileName: '$title.mp4',
                fileSize: directVideoSize,
                isAudioOnly: false,
              ),
            ];

            if (directAudioUrl != null) {
              streams.add(MediaStreamOption(
                streamType: 'audio',
                title: '$title (HQ Audio MP3)',
                format: 'mp3',
                quality: 'HQ Audio',
                url: directAudioUrl,
                suggestedFileName: '$title.mp3',
                fileSize: directAudioSize,
                isAudioOnly: true,
              ));
            }

            return MediaProbeResult(
              success: true,
              originalUrl: url,
              title: title,
              directUrl: directVideoUrl,
              directAudioUrl: directAudioUrl,
              suggestedFileName: '$title.mp4',
              fileSize: directVideoSize,
              quality: quality,
              format: 'mp4',
              provider: 'invidious',
              streams: streams,
            );
          }
        }
      } catch (_) {
        continue;
      }
    }

    return MediaProbeResult.failure(url, 'All Invidious instances failed', provider: 'invidious');
  }
}
