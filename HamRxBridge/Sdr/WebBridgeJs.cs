using System.Globalization;

namespace HamRxBridge.Sdr;

/// <summary>
/// JavaScript run inside receiver web pages (WebSDR and the KiwiSDR web UI) to drive them
/// from our panel and read them back. Kept separate from the WPF view so it can be tested
/// in a plain Chromium browser.
/// </summary>
public static class WebBridgeJs
{
    /// <summary>Finds the window (top page or a same-origin frame) that holds the WebSDR functions.</summary>
    public const string FindWebSdr = @"var w=(function(){if(typeof window.setfreqif==='function')return window;
for(var i=0;i<window.frames.length;i++){try{if(typeof window.frames[i].setfreqif==='function')return window.frames[i];}catch(e){}}return null;})();";

    public const string ProbeWebSdr = "(function(){try{" + FindWebSdr + @"
if(!w)return {ok:false};
var k=null;try{k=(typeof w.nominalfreq==='function')?Number(w.nominalfreq()):Number(w.freq);}catch(e){}
var m='';try{m=String(w.mode||'');}catch(e){}
return {ok:true,kHz:(k!==null&&isFinite(k))?k:null,mode:m};}catch(e){return {ok:false};}})()";

    public const string ProbeKiwi = @"(function(){try{
var ok=(typeof ext_tune==='function'&&typeof ext_get_freq==='function');
var hz=null,m='';if(ok){try{hz=Number(ext_get_freq());m=String(ext_get_mode()||'');}catch(e){ok=false;}}
return {ok:ok&&isFinite(hz)&&hz>0,kHz:hz?hz/1000:null,mode:m};}catch(e){return {ok:false};}})()";

    /// <summary>What the page offers, written to the log once so a failed live control can be diagnosed.</summary>
    public const string DiagJs = @"(function(){try{var names=[];for(var k in window){try{if(typeof window[k]==='function'&&/freq|mode|band|tune|volume|mute/i.test(k))names.push(k);}catch(e){}}
return {url:location.href,frames:window.frames.length,funcs:names.slice(0,60).join(' '),canvases:document.querySelectorAll('canvas').length};}catch(e){return {err:String(e)};}})()";

    public static string TuneWebSdrJs(double kHz, string mode) => string.Create(CultureInfo.InvariantCulture, $@"(function(k,m){{try{{{FindWebSdr}
if(!w)return 'nofunc';
var t=(typeof w.bi!=='undefined'&&w.bi&&w.bi.length)?w.bi:((typeof w.bandinfo!=='undefined')?w.bandinfo:null);
if(t&&typeof w.setband==='function'&&typeof w.band!=='undefined'){{
 var n=(typeof w.nvbands==='number'&&w.nvbands>0)?Math.min(w.nvbands,t.length):t.length;
 for(var i=0;i<n;i++){{var b=t[i];if(!b||typeof b.centerfreq!=='number')continue;var h=b.samplerate/2+4;
  if(k>b.centerfreq-h&&k<b.centerfreq+h){{if(i!==w.band){{try{{b.vfo=k;}}catch(e){{}}w.setband(i);}}break;}}}}}}
var sm=(typeof w.set_mode==='function')?w.set_mode:null;
if(m&&sm)sm(m);w.setfreqif(k.toFixed(3));if(m&&sm)sm(m);return 'ok';}}catch(e){{return 'err '+e;}}}})({kHz:F3},'{mode}')");

    /// <summary>
    /// Runs before the page's own scripts: routes all Web Audio output through one gain node per
    /// audio context, so our volume slider can control the page. Nothing is sent anywhere.
    /// </summary>
    public const string AudioHookJs = @"(function(){if(window.__hrbHook)return;window.__hrbHook=true;window.__hrbVol=1;var gains=[];
try{var orig=AudioNode.prototype.connect;
AudioNode.prototype.connect=function(dest){try{if(typeof AudioDestinationNode!=='undefined'&&dest instanceof AudioDestinationNode){
var ctx=dest.context;if(!ctx.__hrbGain){var g=ctx.createGain();g.gain.value=window.__hrbVol;orig.call(g,dest);ctx.__hrbGain=g;gains.push(g);}
var a=Array.prototype.slice.call(arguments);a[0]=ctx.__hrbGain;return orig.apply(this,a);}}catch(e){}
return orig.apply(this,arguments);};}catch(e){}
window.__hrbSetVolume=function(v){window.__hrbVol=v;gains.forEach(function(g){try{g.gain.value=v;}catch(e){}});
try{document.querySelectorAll('audio,video').forEach(function(a){a.volume=Math.min(1,v);});}catch(e){}
for(var i=0;i<window.frames.length;i++){try{if(window.frames[i].__hrbSetVolume)window.frames[i].__hrbSetVolume(v);}catch(e){}}return gains.length;};})();";

    /// <summary>
    /// Compact view: find the smallest block that contains all of the page's large canvases
    /// (the waterfall), keep that block exactly as the page built it (scale, markers, labels),
    /// and hide only what lies outside it. Returns "on count width" or "nocanvas". Reversible.
    /// </summary>
    public static string CompactJs(bool on) => @"(function(on){try{var id='__hrb_compact';var st=document.getElementById(id);
document.querySelectorAll('[data-hrb-keep],[data-hrb-box]').forEach(function(e){e.removeAttribute('data-hrb-keep');e.removeAttribute('data-hrb-box');});
if(!on){if(st)st.remove();return 'off';}
var cs=[].slice.call(document.querySelectorAll('canvas')).filter(function(c){var r=c.getBoundingClientRect();return r.width>=300&&r.width*r.height>15000;});
if(!cs.length)return 'nocanvas';
var box=cs[0].parentElement;while(box&&box!==document.body&&!cs.every(function(c){return box.contains(c);}))box=box.parentElement;
if(!box||box===document.body||box===document.documentElement)box=cs[0].parentElement;
box.setAttribute('data-hrb-box','1');var e=box.parentElement;while(e&&e!==document.documentElement){e.setAttribute('data-hrb-keep','1');e=e.parentElement;}
var fresh=!st;if(!st){st=document.createElement('style');st.id=id;(document.head||document.documentElement).appendChild(st);}
st.textContent='[data-hrb-keep]>:not([data-hrb-keep]):not([data-hrb-box]){display:none !important;}'
+'html,body{background:#070B12 !important;margin:0 !important;}'
+'[data-hrb-keep]{margin:0 !important;padding:0 !important;border:0 !important;}';
if(fresh)window.scrollTo(0,0);
var w=Math.ceil(box.getBoundingClientRect().width);return 'on '+cs.length+' '+w;}catch(e){return 'err '+e;}})(" + (on ? "true" : "false") + ")";

    public static string TuneKiwiJs(double kHz, string mode, int lo, int hi) => string.Create(CultureInfo.InvariantCulture, $@"(function(k,m,lo,hi){{try{{
var z=(typeof ext_zoom!=='undefined')?ext_zoom.CUR:undefined;
ext_tune(k,m,z);
if(typeof ext_set_passband==='function'&&lo!==hi)ext_set_passband(lo,hi);
return 'ok';}}catch(e){{return 'err '+e;}}}})({kHz:F3},'{mode}',{lo},{hi})");
}
