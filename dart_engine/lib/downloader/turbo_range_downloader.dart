import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'package:http/http.dart' as http;
import '../resolvers/cobalt_resolver.dart';

class TurboDownloadProgress {
  final int downloadedBytes;
  final int totalBytes;
  final double speedBytesPerSec;
  final double percentage;
  final String status;

  TurboDownloadProgress({
    required this.downloadedBytes,
    required this.totalBytes,
    required this.speedBytesPerSec,
    required this.percentage,
    required this.status,
  });

  Map<String, dynamic> toJson() => {
    'type': 'progress',
    'downloadedBytes': downloadedBytes,
    'totalBytes': totalBytes,
    'speed': speedBytesPerSec,
    'percentage': percentage,
    'status': status,
  };
}

class _DownloadChunk {
  final int index;
  final int start;
  final int end;
  int downloaded = 0;

  _DownloadChunk({required this.index, required this.start, required this.end});

  int get length => end - start + 1;
}

class TurboRangeDownloader {
  static const int defaultChunkSize = 9 * 1024 * 1024; // 9MB throttling bypass blocks
  static const int defaultConcurrency = 8;

  final http.Client _client = http.Client();

  Future<void> download({
    required String url,
    required String destinationPath,
    int? knownTotalBytes,
    int concurrency = defaultConcurrency,
    int chunkSize = defaultChunkSize,
    void Function(TurboDownloadProgress progress)? onProgress,
  }) async {
    final destFile = File(destinationPath);
    if (await destFile.exists()) {
      await destFile.delete();
    }
    await destFile.create(recursive: true);

    int totalBytes = knownTotalBytes ?? 0;
    bool supportsRange = false;

    // 1. Probe server for Range support and Content-Length
    try {
      final probeSize = await CobaltResolver.probeUrlSize(url);
      if (probeSize != null && probeSize > 0) {
        totalBytes = probeSize;
        supportsRange = true;
      }
    } catch (_) {}

    if (totalBytes <= 0) {
      // Direct stream probe
      try {
        final head = await _client.head(Uri.parse(url)).timeout(const Duration(seconds: 5));
        final clen = head.headers['content-length'];
        if (clen != null && int.tryParse(clen) != null) {
          totalBytes = int.parse(clen);
        }
        final ranges = head.headers['accept-ranges'];
        supportsRange = ranges != null && ranges.toLowerCase().contains('bytes');
      } catch (_) {}
    }

    if (!supportsRange || totalBytes <= 0) {
      // Fallback: Direct single stream download
      await _downloadSingleStream(url, destFile, totalBytes, onProgress);
      return;
    }

    // 2. Pre-allocate empty file for multi-threaded random access writes
    final initRaf = await destFile.open(mode: FileMode.write);
    try {
      await initRaf.truncate(totalBytes);
    } finally {
      await initRaf.close();
    }

    // 3. Build chunk schedule (9MB chunks or balanced chunks)
    final chunks = <_DownloadChunk>[];
    int start = 0;
    int chunkIdx = 0;

    // Determine optimal chunk size (ensure at least `concurrency` chunks if totalBytes is large)
    int effChunkSize = chunkSize;
    if (totalBytes / effChunkSize < concurrency && totalBytes >= concurrency * 1024 * 1024) {
      effChunkSize = (totalBytes / concurrency).ceil();
    }

    while (start < totalBytes) {
      int end = min(start + effChunkSize - 1, totalBytes - 1);
      chunks.add(_DownloadChunk(index: chunkIdx++, start: start, end: end));
      start = end + 1;
    }

    int totalDownloaded = 0;
    int lastDownloaded = 0;
    DateTime lastTime = DateTime.now();

    // Timer for regular progress updates
    Timer? progressTimer;
    progressTimer = Timer.periodic(const Duration(milliseconds: 250), (_) {
      final now = DateTime.now();
      final elapsedSec = now.difference(lastTime).inMilliseconds / 1000.0;
      double speed = 0.0;
      if (elapsedSec > 0) {
        speed = (totalDownloaded - lastDownloaded) / elapsedSec;
      }
      lastDownloaded = totalDownloaded;
      lastTime = now;

      final pct = totalBytes > 0 ? (totalDownloaded / totalBytes) * 100.0 : 0.0;
      onProgress?.call(TurboDownloadProgress(
        downloadedBytes: totalDownloaded,
        totalBytes: totalBytes,
        speedBytesPerSec: speed,
        percentage: min(100.0, pct),
        status: 'Downloading',
      ));
    });

    try {
      // 4. Download chunks concurrently using pool of workers
      final queue = List<_DownloadChunk>.from(chunks);
      final activeWorkers = <Future<void>>[];

      Future<void> runWorker() async {
        while (queue.isNotEmpty) {
          final chunk = queue.removeAt(0);
          await _downloadChunkWithRetry(url, destFile, chunk, (bytesRead) {
            totalDownloaded += bytesRead;
          });
        }
      }

      int workerCount = min(concurrency, chunks.length);
      for (int i = 0; i < workerCount; i++) {
        activeWorkers.add(runWorker());
      }

      await Future.wait(activeWorkers);
    } finally {
      progressTimer.cancel();
    }

    // Final completion event
    onProgress?.call(TurboDownloadProgress(
      downloadedBytes: totalBytes,
      totalBytes: totalBytes,
      speedBytesPerSec: 0,
      percentage: 100.0,
      status: 'Completed',
    ));
  }

  Future<void> _downloadChunkWithRetry(
    String url,
    File destFile,
    _DownloadChunk chunk,
    void Function(int bytesRead) onChunkBytesRead,
  ) async {
    int attempts = 0;
    while (attempts < 3) {
      attempts++;
      RandomAccessFile? raf;
      try {
        raf = await destFile.open(mode: FileMode.writeOnlyAppend);
        await raf.setPosition(chunk.start);

        final req = http.Request('GET', Uri.parse(url));
        req.headers['Range'] = 'bytes=${chunk.start}-${chunk.end}';
        req.headers['User-Agent'] = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0';

        final streamed = await _client.send(req);
        if (streamed.statusCode != 206 && streamed.statusCode != 200) {
          throw Exception('HTTP ${streamed.statusCode} for chunk ${chunk.index}');
        }

        await for (final data in streamed.stream) {
          await raf.writeFrom(data);
          onChunkBytesRead(data.length);
        }
        return; // Success!
      } catch (e) {
        if (attempts >= 3) rethrow;
        await Future.delayed(Duration(milliseconds: 300 * attempts));
      } finally {
        await raf?.close();
      }
    }
  }

  Future<void> _downloadSingleStream(
    String url,
    File destFile,
    int totalBytes,
    void Function(TurboDownloadProgress progress)? onProgress,
  ) async {
    final req = http.Request('GET', Uri.parse(url));
    req.headers['User-Agent'] = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) IDMLite/1.0';

    final streamed = await _client.send(req);
    final raf = await destFile.open(mode: FileMode.write);

    int downloaded = 0;
    int lastDownloaded = 0;
    DateTime lastTime = DateTime.now();

    try {
      await for (final data in streamed.stream) {
        await raf.writeFrom(data);
        downloaded += data.length;

        final now = DateTime.now();
        final elapsed = now.difference(lastTime).inMilliseconds;
        if (elapsed >= 250) {
          double speed = (downloaded - lastDownloaded) / (elapsed / 1000.0);
          lastDownloaded = downloaded;
          lastTime = now;

          final pct = totalBytes > 0 ? (downloaded / totalBytes) * 100.0 : 0.0;
          onProgress?.call(TurboDownloadProgress(
            downloadedBytes: downloaded,
            totalBytes: totalBytes > 0 ? totalBytes : downloaded,
            speedBytesPerSec: speed,
            percentage: min(100.0, pct),
            status: 'Downloading',
          ));
        }
      }
    } finally {
      await raf.close();
    }

    onProgress?.call(TurboDownloadProgress(
      downloadedBytes: downloaded,
      totalBytes: totalBytes > 0 ? totalBytes : downloaded,
      speedBytesPerSec: 0,
      percentage: 100.0,
      status: 'Completed',
    ));
  }

  void close() {
    _client.close();
  }
}
