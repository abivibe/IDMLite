import 'package:http/http.dart' as http;

class MediaSizeProbe {
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
}
