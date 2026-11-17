import '../models/media_probe_result.dart';
import 'instagram_resolver.dart';
import 'youtube_explode_resolver.dart';

class SocialResolverChain {
  static Future<MediaProbeResult> resolve(String url) async {
    final uri = Uri.tryParse(url);
    if (uri != null && (uri.scheme == 'https' || uri.scheme == 'http')) {
      final host = uri.host.toLowerCase();
      bool matches(String domain) => host == domain || host.endsWith('.$domain');
      if (matches('youtube.com') || matches('youtu.be')) {
        return YouTubeExplodeResolver.resolve(url);
      }
      if (matches('instagram.com') || matches('cdninstagram.com') || matches('fbcdn.net')) {
        return InstagramResolver.resolve(url);
      }
    }
    return MediaProbeResult.failure(url, 'No direct resolver for this social media link', provider: 'chain');
  }
}
