import 'dart:convert';

class MediaStreamOption {
  final String streamType; // 'video', 'audio', 'muxed'
  final String title;
  final String format; // 'mp4', 'mp3', 'webm'
  final String quality; // '1080p', '720p', '320kbps'
  final String url;
  final String suggestedFileName;
  final int? fileSize;
  final bool isAudioOnly;

  MediaStreamOption({
    required this.streamType,
    required this.title,
    required this.format,
    required this.quality,
    required this.url,
    required this.suggestedFileName,
    this.fileSize,
    this.isAudioOnly = false,
  });

  Map<String, dynamic> toJson() => {
    'streamType': streamType,
    'title': title,
    'format': format,
    'quality': quality,
    'url': url,
    'suggestedFileName': suggestedFileName,
    'fileSize': fileSize,
    'isAudioOnly': isAudioOnly,
  };

  factory MediaStreamOption.fromJson(Map<String, dynamic> json) => MediaStreamOption(
    streamType: json['streamType'] as String? ?? 'video',
    title: json['title'] as String? ?? 'Stream',
    format: json['format'] as String? ?? 'mp4',
    quality: json['quality'] as String? ?? 'HD',
    url: json['url'] as String? ?? '',
    suggestedFileName: json['suggestedFileName'] as String? ?? 'video.mp4',
    fileSize: json['fileSize'] as int?,
    isAudioOnly: json['isAudioOnly'] as bool? ?? false,
  );
}

class MediaProbeResult {
  final bool success;
  final String originalUrl;
  final String title;
  final String directUrl;
  final String? directAudioUrl;
  final String suggestedFileName;
  final int? fileSize;
  final String quality;
  final String format;
  final String provider; // 'youtube_explode', 'cobalt', 'invidious'
  final List<MediaStreamOption> streams;
  final String? errorMessage;

  MediaProbeResult({
    required this.success,
    required this.originalUrl,
    required this.title,
    required this.directUrl,
    this.directAudioUrl,
    required this.suggestedFileName,
    this.fileSize,
    required this.quality,
    required this.format,
    required this.provider,
    required this.streams,
    this.errorMessage,
  });

  Map<String, dynamic> toJson() => {
    'success': success,
    'originalUrl': originalUrl,
    'title': title,
    'directUrl': directUrl,
    'directAudioUrl': directAudioUrl,
    'suggestedFileName': suggestedFileName,
    'fileSize': fileSize,
    'quality': quality,
    'format': format,
    'provider': provider,
    'streams': streams.map((s) => s.toJson()).toList(),
    'errorMessage': errorMessage,
  };

  String toJsonString() => jsonEncode(toJson());

  factory MediaProbeResult.failure(String url, String error, {String provider = 'unknown'}) {
    return MediaProbeResult(
      success: false,
      originalUrl: url,
      title: 'Media',
      directUrl: url,
      suggestedFileName: 'video.mp4',
      quality: 'Unknown',
      format: 'mp4',
      provider: provider,
      streams: [],
      errorMessage: error,
    );
  }
}
