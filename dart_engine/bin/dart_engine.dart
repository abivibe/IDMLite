import 'dart:convert';
import 'dart:io';
import 'package:dart_engine/dart_engine.dart';

void main(List<String> args) async {
  if (args.isEmpty) {
    print('IDMLite Dart Engine CLI');
    print('Usage:');
    print('  dart_engine probe <url>');
    print('  dart_engine download <url> <destPath> [--threads 8] [--chunk-size 9437184] [--size <bytes>]');
    print('  dart_engine probe-and-download <url> <destPath>');
    exit(1);
  }

  final command = args[0].toLowerCase();

  if (command == 'probe') {
    if (args.length < 2) {
      stderr.writeln('Error: URL required for probe');
      exit(1);
    }
    final url = args[1];
    try {
      final result = await SocialResolverChain.resolve(url);
      stdout.writeln(result.toJsonString());
      exit(result.success ? 0 : 2);
    } catch (e) {
      final fail = MediaProbeResult.failure(url, e.toString());
      stdout.writeln(fail.toJsonString());
      exit(2);
    }
  } else if (command == 'download') {
    if (args.length < 3) {
      stderr.writeln('Error: URL and destination path required');
      exit(1);
    }
    final url = args[1];
    final destPath = args[2];

    int concurrency = TurboRangeDownloader.defaultConcurrency;
    int chunkSize = TurboRangeDownloader.defaultChunkSize;
    int? knownSize;

    for (int i = 3; i < args.length; i++) {
      if (args[i] == '--threads' && i + 1 < args.length) {
        concurrency = int.tryParse(args[++i]) ?? concurrency;
      } else if (args[i] == '--chunk-size' && i + 1 < args.length) {
        chunkSize = int.tryParse(args[++i]) ?? chunkSize;
      } else if (args[i] == '--size' && i + 1 < args.length) {
        knownSize = int.tryParse(args[++i]);
      }
    }

    final downloader = TurboRangeDownloader();
    try {
      await downloader.download(
        url: url,
        destinationPath: destPath,
        knownTotalBytes: knownSize,
        concurrency: concurrency,
        chunkSize: chunkSize,
        onProgress: (progress) {
          stdout.writeln(jsonEncode(progress.toJson()));
        },
      );
      stdout.writeln(jsonEncode({
        'type': 'completed',
        'filePath': destPath,
        'success': true,
      }));
      exit(0);
    } catch (e) {
      stderr.writeln('Download failed: $e');
      stdout.writeln(jsonEncode({
        'type': 'error',
        'error': e.toString(),
        'success': false,
      }));
      exit(3);
    } finally {
      downloader.close();
    }
  } else if (command == 'probe-and-download') {
    if (args.length < 3) {
      stderr.writeln('Error: URL and destination path required');
      exit(1);
    }
    final url = args[1];
    final destPath = args[2];

    final probe = await SocialResolverChain.resolve(url);
    if (!probe.success || probe.directUrl.isEmpty) {
      stderr.writeln('Probe failed: ${probe.errorMessage}');
      exit(2);
    }

    stdout.writeln(probe.toJsonString());

    final downloader = TurboRangeDownloader();
    try {
      await downloader.download(
        url: probe.directUrl,
        destinationPath: destPath,
        knownTotalBytes: probe.fileSize,
        onProgress: (progress) {
          stdout.writeln(jsonEncode(progress.toJson()));
        },
      );
      stdout.writeln(jsonEncode({
        'type': 'completed',
        'filePath': destPath,
        'success': true,
      }));
      exit(0);
    } catch (e) {
      stderr.writeln('Download failed: $e');
      exit(3);
    } finally {
      downloader.close();
    }
  } else if (command == 'yt-download') {
    if (args.length < 3) {
      stderr.writeln('Error: URL and destination path required');
      exit(1);
    }
    final url = args[1];
    final destPath = args[2];
    String? ffmpegPath;
    int speedLimit = 0;
    for (int i = 3; i < args.length; i++) {
      if (args[i] == '--ffmpeg' && i + 1 < args.length) {
        ffmpegPath = args[++i];
      } else if (args[i] == '--limit-rate' && i + 1 < args.length) {
        speedLimit = int.tryParse(args[++i]) ?? 0;
      }
    }

    try {
      await YouTubeExplodeResolver.downloadYouTube(
        url: url,
        destinationPath: destPath,
        ffmpegPath: ffmpegPath,
        speedLimit: speedLimit,
        onProgress: (progress) {
          stdout.writeln(jsonEncode(progress.toJson()));
        },
      );
      stdout.writeln(jsonEncode({
        'type': 'completed',
        'filePath': destPath,
        'success': true,
      }));
      exit(0);
    } catch (e) {
      stderr.writeln('YouTubeExplode download failed: $e');
      stdout.writeln(jsonEncode({
        'type': 'error',
        'error': e.toString(),
        'success': false,
      }));
      exit(3);
    }
  } else {
    stderr.writeln('Unknown command: $command');
    exit(1);
  }
}
