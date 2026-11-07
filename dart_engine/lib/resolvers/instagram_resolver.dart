import 'dart:convert';
import 'package:http/http.dart' as http;
import '../models/media_probe_result.dart';
import 'cobalt_resolver.dart';

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
      int? size = await CobaltResolver.probeUrlSize(url);
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

    // 1. SnapSave Çözücü
    try {
      directUrl = await _resolveSnapSave(url);
    } catch (_) {}

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
      int? size = await CobaltResolver.probeUrlSize(directUrl);
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
        provider: 'snapsave',
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

  static Future<String?> _resolveSnapSave(String targetUrl) async {
    final resp = await http.post(
      Uri.parse('https://snapsave.app/action.php'),
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
        'User-Agent':
            'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
        'Origin': 'https://snapsave.app',
        'Referer': 'https://snapsave.app/',
      },
      body: {'url': targetUrl},
    ).timeout(const Duration(seconds: 8));

    if (resp.statusCode == 200 && resp.body.isNotEmpty) {
      final body = resp.body;
      final evalRegex = RegExp(r'\(\s*"([^"]+)"\s*,\s*[^,]*\s*,\s*"([^"]+)"\s*,\s*(\d+)\s*,\s*(\d+)');
      final match = evalRegex.firstMatch(body);
      if (match != null) {
        final h = match.group(1)!;
        final n = match.group(2)!;
        final t = int.tryParse(match.group(3)!) ?? 0;
        final e = int.tryParse(match.group(4)!) ?? 10;
        final unpackedHtml = _decodeSnapApp(h, n, t, e);

        final hrefRegex = RegExp(r'href="([^"]+)"');
        final hrefMatches = hrefRegex.allMatches(unpackedHtml);
        for (final m in hrefMatches) {
          final link = m.group(1);
          if (link != null &&
              (link.contains('.mp4') ||
               link.contains('cdninstagram.com') ||
               link.contains('fbcdn.net') ||
               link.contains('snapcdn'))) {
            return link.replaceAll('&amp;', '&');
          }
        }
      }
    }
    return null;
  }

  static String _decodeSnapApp(String h, String n, int t, int e) {
    const alphabet = '0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ+/';
    final hCharset = e <= alphabet.length ? alphabet.substring(0, e) : alphabet;

    final buffer = StringBuffer();
    var i = 0;
    final len = h.length;
    final neChar = e < n.length ? n[e] : null;

    while (i < len) {
      var s = '';
      while (i < len && (neChar == null || h[i] != neChar)) {
        s += h[i];
        i++;
      }
      i++;

      for (var j = 0; j < n.length; j++) {
        s = s.replaceAll(n[j], j.toString());
      }

      if (s.isNotEmpty) {
        var intVal = 0;
        final digits = s.split('').reversed.toList();
        for (var c = 0; c < digits.length; c++) {
          final char = digits[c];
          final idx = hCharset.indexOf(char);
          if (idx != -1) {
            intVal += idx * _intPow(e, c);
          }
        }
        final code = intVal - t;
        if (code > 0) {
          buffer.writeCharCode(code);
        }
      }
    }

    return utf8.decode(buffer.toString().codeUnits, allowMalformed: true);
  }

  static int _intPow(int base, int exp) {
    var result = 1;
    for (var i = 0; i < exp; i++) {
      result *= base;
    }
    return result;
  }
}
