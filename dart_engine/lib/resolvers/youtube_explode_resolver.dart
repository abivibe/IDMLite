import 'dart:io';
import 'dart:async';
import 'package:youtube_explode_dart/youtube_explode_dart.dart';
import '../models/media_probe_result.dart';
import '../downloader/turbo_range_downloader.dart';

class YouTubeExplodeResolver {
  static final _invalidCharsRegex = RegExp(r'[/\\?%*:|"<>]+');

  static String sanitizeFileName(String name) {
    var clean = name.replaceAll(_invalidCharsRegex, '_').trim();
    if (clean.isEmpty) clean = 'YouTube_Video';
    return clean;
  }

  static bool canResolve(String url) {
    final lower = url.toLowerCase();
    return lower.contains('youtube.com/watch') ||
        lower.contains('youtu.be/') ||
        lower.contains('youtube.com/shorts/') ||
        lower.contains('youtube.com/embed/') ||
        lower.contains('m.youtube.com/watch');
  }

  static Future<MediaProbeResult> resolve(String url) async {
    final yt = YoutubeExplode();
    try {
      final video = await yt.videos.get(url).timeout(const Duration(seconds: 10));
      final title = video.title;
      final safeTitle = sanitizeFileName(title);

      final manifest = await yt.videos.streamsClient.getManifest(video.id).timeout(const Duration(seconds: 12));

      final streams = <MediaStreamOption>[];

      AudioOnlyStreamInfo? bestAudio;
      if (manifest.audioOnly.isNotEmpty) {
        final m4aStreams = manifest.audioOnly
            .where((s) =>
                s.container.name.toLowerCase() == 'mp4' ||
                s.audioCodec.toLowerCase().startsWith('mp4a'))
            .toList();
        bestAudio = m4aStreams.isNotEmpty
            ? m4aStreams.withHighestBitrate()
            : manifest.audioOnly.withHighestBitrate();
      }

      VideoOnlyStreamInfo? bestVideo;
      if (manifest.videoOnly.isNotEmpty) {
        final avcStreams = manifest.videoOnly
            .where((s) => s.videoCodec.toLowerCase().startsWith('avc1'))
            .toList();
        bestVideo = avcStreams.isNotEmpty
            ? avcStreams.withHighestBitrate()
            : manifest.videoOnly.withHighestBitrate();
      }

      MuxedStreamInfo? bestMuxed;
      if (manifest.muxed.isNotEmpty) {
        bestMuxed = manifest.muxed.withHighestBitrate();
      }

      int? primaryVideoSize;
      String primaryQuality = '1080p HD';

      if (bestVideo != null && bestAudio != null) {
        primaryVideoSize = bestVideo.size.totalBytes + bestAudio.size.totalBytes;
        primaryQuality = '${bestVideo.qualityLabel} HD';
      } else if (bestMuxed != null) {
        primaryVideoSize = bestMuxed.size.totalBytes;
        primaryQuality = bestMuxed.qualityLabel;
      }

      // 1. Primary Full Quality Video Option (keep canonical YouTube URL so yt-dlp/YouTubeExplode muxes video+audio)
      streams.add(MediaStreamOption(
        streamType: 'video',
        title: '$safeTitle ($primaryQuality MP4)',
        format: 'mp4',
        quality: primaryQuality,
        url: url,
        suggestedFileName: '$safeTitle.mp4',
        fileSize: primaryVideoSize,
        isAudioOnly: false,
      ));

      // 2. High Quality Audio Option
      if (bestAudio != null) {
        streams.add(MediaStreamOption(
          streamType: 'audio',
          title: '$safeTitle (En Yüksek Kalite Ses)',
          format: 'mp3',
          quality: '${(bestAudio.bitrate.kiloBitsPerSecond).round()} kbps',
          url: url,
          suggestedFileName: '$safeTitle.mp3',
          fileSize: bestAudio.size.totalBytes,
          isAudioOnly: true,
        ));
      }

      // 3. Muxed single-piece stream (if available)
      if (bestMuxed != null) {
        streams.add(MediaStreamOption(
          streamType: 'muxed',
          title: '$safeTitle (${bestMuxed.qualityLabel} Tek Parça)',
          format: 'mp4',
          quality: bestMuxed.qualityLabel,
          url: url,
          suggestedFileName: '${safeTitle}_${bestMuxed.qualityLabel}.mp4',
          fileSize: bestMuxed.size.totalBytes,
          isAudioOnly: false,
        ));
      }

      // 4. Distinct video qualities
      final seenQualities = <String>{primaryQuality};
      for (final v in manifest.videoOnly) {
        final q = v.qualityLabel;
        if (!seenQualities.contains(q)) {
          seenQualities.add(q);
          final combinedSize = bestAudio != null ? (v.size.totalBytes + bestAudio.size.totalBytes) : v.size.totalBytes;
          streams.add(MediaStreamOption(
            streamType: 'video',
            title: '$safeTitle ($q MP4)',
            format: 'mp4',
            quality: q,
            url: url,
            suggestedFileName: '${safeTitle}_$q.mp4',
            fileSize: combinedSize,
            isAudioOnly: false,
          ));
        }
      }

      return MediaProbeResult(
        success: true,
        originalUrl: url,
        title: title,
        directUrl: url,
        directAudioUrl: bestAudio?.url.toString(),
        suggestedFileName: '$safeTitle.mp4',
        fileSize: primaryVideoSize,
        quality: primaryQuality,
        format: 'mp4',
        provider: 'youtube_explode',
        streams: streams,
      );
    } catch (e) {
      return MediaProbeResult.failure(url, e.toString(), provider: 'youtube_explode');
    } finally {
      yt.close();
    }
  }

  static Future<void> downloadYouTube({
    required String url,
    required String destinationPath,
    String? ffmpegPath,
    void Function(TurboDownloadProgress)? onProgress,
  }) async {
    final yt = YoutubeExplode();
    try {
      final video = await yt.videos.get(url).timeout(const Duration(seconds: 12));
      final manifest = await yt.videos.streamsClient.getManifest(video.id).timeout(const Duration(seconds: 15));

      final isAudioOnly = destinationPath.toLowerCase().endsWith('.mp3') ||
          destinationPath.toLowerCase().endsWith('.m4a');

      AudioOnlyStreamInfo? bestAudio;
      if (manifest.audioOnly.isNotEmpty) {
        final m4aStreams = manifest.audioOnly
            .where((s) =>
                s.container.name.toLowerCase() == 'mp4' ||
                s.audioCodec.toLowerCase().startsWith('mp4a'))
            .toList();
        bestAudio = m4aStreams.isNotEmpty
            ? m4aStreams.withHighestBitrate()
            : manifest.audioOnly.withHighestBitrate();
      }

      if (isAudioOnly && bestAudio != null) {
        final totalBytes = bestAudio.size.totalBytes;
        int downloaded = 0;
        final sw = Stopwatch()..start();
        final file = File(destinationPath);
        await file.parent.create(recursive: true);
        final sink = file.openWrite();
        await for (final chunk in yt.videos.streamsClient.get(bestAudio).timeout(const Duration(seconds: 25))) {
          sink.add(chunk);
          downloaded += chunk.length;
          final elapsed = sw.elapsedMilliseconds / 1000.0;
          final speed = elapsed > 0 ? downloaded / elapsed : 0.0;
          onProgress?.call(TurboDownloadProgress(
            downloadedBytes: downloaded,
            totalBytes: totalBytes,
            speedBytesPerSec: speed,
            percentage: totalBytes > 0 ? (downloaded / totalBytes * 100.0) : 0.0,
            status: 'downloading',
          ));
        }
        await sink.flush();
        await sink.close();
        return;
      }

      VideoOnlyStreamInfo? bestVideo;
      if (manifest.videoOnly.isNotEmpty) {
        final avcStreams = manifest.videoOnly
            .where((s) => s.videoCodec.toLowerCase().startsWith('avc1'))
            .toList();
        bestVideo = avcStreams.isNotEmpty
            ? avcStreams.withHighestBitrate()
            : manifest.videoOnly.withHighestBitrate();
      }

      if (bestVideo != null && bestAudio != null && ffmpegPath != null && ffmpegPath.isNotEmpty && File(ffmpegPath).existsSync()) {
        final tempVideo = '$destinationPath.ytv.mp4';
        final tempAudio = '$destinationPath.yta.m4a';
        final totalBytes = bestVideo.size.totalBytes + bestAudio.size.totalBytes;
        int downloaded = 0;
        final sw = Stopwatch()..start();

        await File(destinationPath).parent.create(recursive: true);

        final vSink = File(tempVideo).openWrite();
        await for (final chunk in yt.videos.streamsClient.get(bestVideo).timeout(const Duration(seconds: 25))) {
          vSink.add(chunk);
          downloaded += chunk.length;
          final elapsed = sw.elapsedMilliseconds / 1000.0;
          final speed = elapsed > 0 ? downloaded / elapsed : 0.0;
          onProgress?.call(TurboDownloadProgress(
            downloadedBytes: downloaded,
            totalBytes: totalBytes,
            speedBytesPerSec: speed,
            percentage: totalBytes > 0 ? (downloaded / totalBytes * 100.0).clamp(0.0, 99.0) : 0.0,
            status: 'downloading',
          ));
        }
        await vSink.flush();
        await vSink.close();

        final aSink = File(tempAudio).openWrite();
        await for (final chunk in yt.videos.streamsClient.get(bestAudio).timeout(const Duration(seconds: 25))) {
          aSink.add(chunk);
          downloaded += chunk.length;
          final elapsed = sw.elapsedMilliseconds / 1000.0;
          final speed = elapsed > 0 ? downloaded / elapsed : 0.0;
          onProgress?.call(TurboDownloadProgress(
            downloadedBytes: downloaded,
            totalBytes: totalBytes,
            speedBytesPerSec: speed,
            percentage: totalBytes > 0 ? (downloaded / totalBytes * 100.0).clamp(0.0, 99.5) : 0.0,
            status: 'downloading',
          ));
        }
        await aSink.flush();
        await aSink.close();

        final res = await Process.run(ffmpegPath, [
          '-y',
          '-i',
          tempVideo,
          '-i',
          tempAudio,
          '-c',
          'copy',
          '-movflags',
          '+faststart',
          destinationPath,
        ]);

        try {
          if (File(tempVideo).existsSync()) File(tempVideo).deleteSync();
        } catch (_) {}
        try {
          if (File(tempAudio).existsSync()) File(tempAudio).deleteSync();
        } catch (_) {}

        if (res.exitCode != 0) {
          throw Exception('FFmpeg muxing failed with exit code ${res.exitCode}');
        }
        return;
      }

      if (manifest.muxed.isNotEmpty) {
        final muxed = manifest.muxed.withHighestBitrate();
        final totalBytes = muxed.size.totalBytes;
        int downloaded = 0;
        final sw = Stopwatch()..start();
        final file = File(destinationPath);
        await file.parent.create(recursive: true);
        final sink = file.openWrite();
        await for (final chunk in yt.videos.streamsClient.get(muxed).timeout(const Duration(seconds: 25))) {
          sink.add(chunk);
          downloaded += chunk.length;
          final elapsed = sw.elapsedMilliseconds / 1000.0;
          final speed = elapsed > 0 ? downloaded / elapsed : 0.0;
          onProgress?.call(TurboDownloadProgress(
            downloadedBytes: downloaded,
            totalBytes: totalBytes,
            speedBytesPerSec: speed,
            percentage: totalBytes > 0 ? (downloaded / totalBytes * 100.0) : 0.0,
            status: 'downloading',
          ));
        }
        await sink.flush();
        await sink.close();
        return;
      }

      throw Exception('No downloadable YouTube streams found');
    } finally {
      yt.close();
    }
  }
}

