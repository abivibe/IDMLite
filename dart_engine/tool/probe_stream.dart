import 'dart:io';
import 'package:youtube_explode_dart/youtube_explode_dart.dart';
Future<void> main() async {
 for(final pair in {'ios':YoutubeApiClient.ios,'vr':YoutubeApiClient.androidVr,'tv':YoutubeApiClient.tv,'media':YoutubeApiClient.mediaConnect}.entries){
 final yt=YoutubeExplode();try{
 final m=await yt.videos.streamsClient.getManifest('mJYxSed0D_I',ytClients:[pair.value]).timeout(Duration(seconds:15));
 final s=m.videoOnly.where((v)=>v.videoCodec.startsWith('avc1')).withHighestBitrate();
 final client=HttpClient()..connectionTimeout=Duration(seconds:8)..autoUncompress=false;
 try{final end=s.size.totalBytes-1;final start=end-1023;final r=await client.getUrl(s.url).timeout(Duration(seconds:10));r.headers.set('Range','bytes=$start-$end');final res=await r.close().timeout(Duration(seconds:10));int size=0;await for(final c in res.timeout(Duration(seconds:10))){size+=c.length;if(size>1024)break;}print('client=${pair.key} tag=${s.tag} size=${s.size.totalBytes} tail=${res.statusCode} range=${res.headers.value('content-range')} received=$size');}catch(e){print('client=${pair.key} failed ${e.runtimeType}');}finally{client.close(force:true);}
 }catch(e){print('client=${pair.key} manifest failed ${e.runtimeType}');}finally{yt.close();}
 }
}
