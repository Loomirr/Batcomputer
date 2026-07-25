using Microsoft.Web.WebView2.WinForms;

namespace Batcomputer;

/// <summary>
/// A read-only 3D preview hosted in an embedded WebView2 (offline - no browser, no network). The
/// page renders with WebGL inside this window. Step 1 shows a WebGL smoke test to prove the embed
/// works on the machine; later it loads the decoded character glTF.
/// </summary>
public sealed class ModelPreviewForm : Form
{
    // A UNIQUE host per preview. The folder behind a fixed host changes every build, and WebView2
    // keeps serving the previous page's models.js and .glb from cache - which renders as a stuck
    // camera inside a different character's geometry. A fresh host name has an empty cache.
    private readonly string _virtualHost = $"p{Guid.NewGuid():N}.batcomputer";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string? _html;
    private readonly string? _folder;

    /// <summary>Renders an inline HTML string (used for the WebGL smoke test).</summary>
    public ModelPreviewForm(string html, string title = "Character preview")
    {
        _html = html;
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 720);
        BackColor = Theme.WindowBg;
        Icon = EmbeddedAssets.LoadIcon("Icon.ico") ?? Icon;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    /// <summary>Serves a preview folder (index.html + model.glb + three.js) over a virtual host.</summary>
    public static ModelPreviewForm ForFolder(string folder, string title = "Character preview")
        => new(folder, title, isFolder: true);

    private ModelPreviewForm(string folder, string title, bool isFolder)
    {
        _folder = folder;
        Text = title;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 760);
        BackColor = Theme.WindowBg;
        Icon = EmbeddedAssets.LoadIcon("Icon.ico") ?? Icon;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // Each preview gets its OWN WebView2 user-data folder. The default folder is derived from
            // the exe path, so a second instance (or a leftover msedgewebview2 child from a previous
            // run) holds a lock on it and the next launch dies with 0x800700AA "resource in use".
            var userData = Path.Combine(Path.GetTempPath(), "Batcomputer.WebView2",
                Environment.ProcessId.ToString());
            Directory.CreateDirectory(userData);
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.DefaultBackgroundColor = Theme.WindowBg;
            if (_folder is not null)
            {
                // Serve the local preview folder over a fake https host - this supports fetching the
                // .glb and the module scripts, which NavigateToString's about:blank origin does not.
                _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    _virtualHost, _folder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                // Cache-bust: the virtual host name is constant while the folder behind it changes
                // per build, so WebView2 happily serves a PREVIOUS character's index.html/models.js
                // - which renders as a stuck camera inside stale geometry.
                _web.CoreWebView2.Navigate($"https://{_virtualHost}/index.html");
            }
            else
            {
                _web.NavigateToString(_html!);
            }
        }
        catch (Exception ex)
        {
            // Almost always a missing WebView2 Runtime - say so plainly rather than crashing.
            Controls.Remove(_web);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.OnDark,
                Font = Theme.Body,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "The 3D preview needs the Microsoft WebView2 Runtime, which could not start.\n\n"
                     + ex.Message,
            });
        }
    }

    /// <summary>Minimal WebGL smoke test: a spinning cube, no external scripts. In-app render proof.</summary>
    public static string WebGlSmokeTestHtml() => """
<!doctype html><html><head><meta charset="utf-8"><style>
  html,body{margin:0;height:100%;background:#1a1d22;overflow:hidden;font-family:Segoe UI,sans-serif}
  #hud{position:absolute;left:12px;top:10px;color:#9ea6b2;font-size:13px}
  #hud b{color:#f0c230}
  canvas{display:block;width:100vw;height:100vh}
</style></head><body>
<div id="hud"><b>WebView2 + WebGL</b> — in-app render test</div>
<canvas id="c"></canvas>
<script>
const cv=document.getElementById('c');
const gl=cv.getContext('webgl');
function size(){cv.width=innerWidth;cv.height=innerHeight;gl.viewport(0,0,cv.width,cv.height);}
addEventListener('resize',size);size();
const vs=`attribute vec3 p;attribute vec3 n;uniform mat4 mvp;uniform mat4 mv;varying vec3 vn;
void main(){gl_Position=mvp*vec4(p,1.0);vn=mat3(mv)*n;}`;
const fs=`precision mediump float;varying vec3 vn;
void main(){vec3 L=normalize(vec3(.4,.8,.6));float d=max(dot(normalize(vn),L),0.0)*0.8+0.2;
gl_FragColor=vec4(vec3(.94,.76,.19)*d,1.0);}`;
function sh(t,s){const o=gl.createShader(t);gl.shaderSource(o,s);gl.compileShader(o);return o;}
const pr=gl.createProgram();gl.attachShader(pr,sh(gl.VERTEX_SHADER,vs));gl.attachShader(pr,sh(gl.FRAGMENT_SHADER,fs));
gl.linkProgram(pr);gl.useProgram(pr);
const V=[[-1,-1,-1],[1,-1,-1],[1,1,-1],[-1,1,-1],[-1,-1,1],[1,-1,1],[1,1,1],[-1,1,1]];
const F=[[0,1,2,3,0,0,-1],[4,5,6,7,0,0,1],[0,4,7,3,-1,0,0],[1,5,6,2,1,0,0],[3,2,6,7,0,1,0],[0,1,5,4,0,-1,0]];
let P=[],N=[],I=[],vc=0;
for(const f of F){const n=[f[4],f[5],f[6]];const q=[f[0],f[1],f[2],f[3]];
for(const idx of q){P.push(...V[idx]);N.push(...n);}I.push(vc,vc+1,vc+2,vc,vc+2,vc+3);vc+=4;}
function buf(data,attr,size){const b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);
gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(data),gl.STATIC_DRAW);
const l=gl.getAttribLocation(pr,attr);gl.enableVertexAttribArray(l);gl.vertexAttribPointer(l,size,gl.FLOAT,false,0,0);}
buf(P,'p',3);buf(N,'n',3);
const ib=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,ib);
gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint16Array(I),gl.STATIC_DRAW);
gl.enable(gl.DEPTH_TEST);
function mul(a,b){const o=new Array(16).fill(0);for(let r=0;r<4;r++)for(let c=0;c<4;c++)for(let k=0;k<4;k++)o[c*4+r]+=a[k*4+r]*b[c*4+k];return o;}
function persp(f,a,n,fa){const t=1/Math.tan(f/2);return[t/a,0,0,0, 0,t,0,0, 0,0,(fa+n)/(n-fa),-1, 0,0,2*fa*n/(n-fa),0];}
function rot(ax,ay){const cx=Math.cos(ax),sx=Math.sin(ax),cy=Math.cos(ay),sy=Math.sin(ay);
const rx=[1,0,0,0, 0,cx,sx,0, 0,-sx,cx,0, 0,0,0,1];const ry=[cy,0,-sy,0, 0,1,0,0, sy,0,cy,0, 0,0,0,1];return mul(ry,rx);}
const mvpL=gl.getUniformLocation(pr,'mvp'),mvL=gl.getUniformLocation(pr,'mv');
let t=0;
function frame(){t+=0.012;gl.clearColor(0.102,0.114,0.133,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
let mv=mul([1,0,0,0,0,1,0,0,0,0,1,0,0,0,-6,1],rot(t*0.7,t));
const mvp=mul(persp(1.1,cv.width/cv.height,0.1,100),mv);
gl.uniformMatrix4fv(mvpL,false,new Float32Array(mvp));gl.uniformMatrix4fv(mvL,false,new Float32Array(mv));
gl.drawElements(gl.TRIANGLES,I.length,gl.UNSIGNED_SHORT,0);requestAnimationFrame(frame);}
frame();
</script></body></html>
""";
}
