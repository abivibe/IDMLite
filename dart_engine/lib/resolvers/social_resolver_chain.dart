import '../models/media_probe_result.dart';
import 'youtube_explode_resolver.dart';
import 'cobalt_resolver.dart';
import 'invidious_resolver.dart';
import 'twitter_resolver.dart';
import 'instagram_resolver.dart';
import 'tiktok_resolver.dart';

class SocialResolverChain {
  static Future<MediaProbeResult> resolve(String url) async {
    final lower = url.toLowerCase();

    // 1. YouTube
    if (lower.contains('youtube.com/') || lower.contains('youtu.be/')) {
      final ytResult = await YouTubeExplodeResolver.resolve(url);
      if (ytResult.success && ytResult.directUrl.isNotEmpty) {
        return ytResult;
      }

      final cobaltResult = await CobaltResolver.resolve(url);
      if (cobaltResult.success && cobaltResult.directUrl.isNotEmpty) {
        return cobaltResult;
      }

      final invidiousResult = await InvidiousResolver.resolve(url);
      if (invidiousResult.success && invidiousResult.directUrl.isNotEmpty) {
        return invidiousResult;
      }

      return ytResult.errorMessage != null
          ? ytResult
          : MediaProbeResult.failure(url, 'All YouTube resolvers exhausted', provider: 'chain');
    }

    // 2. X (Twitter)
    if (lower.contains('twitter.com/') || lower.contains('x.com/')) {
      final twResult = await TwitterResolver.resolve(url);
      if (twResult.success && twResult.directUrl.isNotEmpty) {
        return twResult;
      }

      final cobaltResult = await CobaltResolver.resolve(url);
      if (cobaltResult.success && cobaltResult.directUrl.isNotEmpty) {
        return cobaltResult;
      }

      return twResult;
    }

    // 3. Instagram
    if (lower.contains('instagram.com/')) {
      final igResult = await InstagramResolver.resolve(url);
      if (igResult.success && igResult.directUrl.isNotEmpty) {
        return igResult;
      }

      final cobaltResult = await CobaltResolver.resolve(url);
      if (cobaltResult.success && cobaltResult.directUrl.isNotEmpty) {
        return cobaltResult;
      }

      return igResult;
    }

    // 4. TikTok
    if (lower.contains('tiktok.com/')) {
      final ttResult = await TikTokResolver.resolve(url);
      if (ttResult.success && ttResult.directUrl.isNotEmpty) {
        return ttResult;
      }

      final cobaltResult = await CobaltResolver.resolve(url);
      if (cobaltResult.success && cobaltResult.directUrl.isNotEmpty) {
        return cobaltResult;
      }

      return ttResult;
    }

    // 5. Generic fallback to Cobalt
    final genericResult = await CobaltResolver.resolve(url);
    if (genericResult.success && genericResult.directUrl.isNotEmpty) {
      return genericResult;
    }

    return MediaProbeResult.failure(url, 'Failed to resolve social media link', provider: 'chain');
  }
}
