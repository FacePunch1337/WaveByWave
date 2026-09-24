using System;
using System.Globalization;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace WaveByWave.Combat
{
    // DOTS presentation: a bounded NativeArray pool, Burst parallel animation and
    // glyph generation, one mesh/atlas draw for all hits. No GameObject per popup.
    public sealed class DotsDamagePopups : MonoBehaviour
    {
        private const string Channel = "WaveByWave.DamageNumbers.v1";
        private const int Characters = 10, PacketSize = 48;
        private static DotsDamagePopups _instance;
        private struct Hit { public Vector3 Position; public float Amount; }
        private struct Popup
        {
            public float3 Position;
            public float Age, Lifetime;
            public FixedString32Bytes Text;
        }
        private struct Glyph { public float2 Min, Max, UV0, UV1, UV2, UV3; public float Advance; }
        private struct Vertex { public float3 Position; public Color32 Color; public float2 UV; }
        private NativeArray<Popup> _popups;
        private NativeArray<Glyph> _glyphs;
        private NativeArray<Vertex> _vertices;
        private readonly Hit[] _pending = new Hit[PacketSize];
        private int _pendingCount, _cursor, _liveFrames;
        private NetworkManager _network;
        private CustomMessagingManager _messages;
        private DamagePopupSettings _settings;
        private Font _font;
        private bool _fontDirty;
        private Mesh _mesh;
        private Material _material;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            var go = new GameObject("DOTS damage numbers");
            _instance = go.AddComponent<DotsDamagePopups>();
            DontDestroyOnLoad(go);
        }
        private void Awake()
        {
            _instance = this;
            _settings = Resources.Load<DamagePopupSettings>("DamagePopupSettings");
            if (_settings == null) _settings = ScriptableObject.CreateInstance<DamagePopupSettings>();
            _font = _settings.Font != null ? _settings.Font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var capacity = Mathf.Clamp(_settings.MaximumVisiblePopups,64,8192);
            _popups = new NativeArray<Popup>(capacity,Allocator.Persistent);
            _glyphs = new NativeArray<Glyph>(128,Allocator.Persistent);
            _vertices = new NativeArray<Vertex>(capacity*Characters*4,Allocator.Persistent);
            _mesh = new Mesh { name = "Batched damage glyphs", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _mesh.SetVertexBufferParams(_vertices.Length,
                new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
                new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.UNorm8,4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2));
            var indices = new int[capacity*Characters*6];
            for (var i=0;i<capacity*Characters;i++)
            {
                var v=i*4;var t=i*6;
                indices[t]=v;indices[t+1]=v+1;indices[t+2]=v+2;
                indices[t+3]=v;indices[t+4]=v+2;indices[t+5]=v+3;
            }
            _mesh.SetIndices(indices,MeshTopology.Triangles,0,false);
            var shader = _settings.Shader != null ? _settings.Shader : Shader.Find("WaveByWave/UI/Damage Glyphs");
            if (shader != null) _material = new Material(shader);
            Font.textureRebuilt += FontRebuilt;
            _fontDirty=true;
            SceneManager.sceneLoaded += SceneLoaded;
        }
        private void SceneLoaded(Scene scene,LoadSceneMode mode)
        {
            for(var i=0;i<_popups.Length;i++) _popups[i]=default;
            _pendingCount=0;_liveFrames=0;
        }
        private void FontRebuilt(Font font) { if(font==_font) _fontDirty=true; }
        private void RebuildGlyphs()
        {
            _font.RequestCharactersInTexture("0123456789.-",_settings.AtlasFontSize,_settings.FontStyle);
            foreach(var c in "0123456789.-")
            {
                if(!_font.GetCharacterInfo(c,out var info,_settings.AtlasFontSize,_settings.FontStyle)) continue;
                _glyphs[c]=new Glyph { Min=new float2(info.minX,info.minY),Max=new float2(info.maxX,info.maxY),
                    UV0=info.uvBottomLeft,UV1=info.uvTopLeft,UV2=info.uvTopRight,UV3=info.uvBottomRight,Advance=info.advance };
            }
            _material.mainTexture=_font.material.mainTexture;
            _material.SetColor("_OutlineColor",_settings.OutlineColor);
            _material.SetFloat("_OutlinePixels",_settings.OutlinePixels);
            _fontDirty=false;
        }
        public static void ReportServer(Vector3 position,float amount)
        {
            if (!float.IsFinite(amount) || amount <= 0f) return;
            if (_instance == null) Bootstrap();
            var self=_instance; var manager=NetworkManager.Singleton;
            if(manager != null && manager.IsListening && !manager.IsServer) return;
            if(manager == null || !manager.IsListening || manager.IsClient) self.Add(position,amount);
            if(manager == null || !manager.IsServer) return;
            self.BindNetwork();
            self._pending[self._pendingCount++]=new Hit {Position=position,Amount=amount};
            if(self._pendingCount==PacketSize) self.Flush();
        }
        private void Add(Vector3 position,float amount)
        {
            var camera=Camera.main;
            if(camera==null || (camera.transform.position-position).sqrMagnitude > _settings.MaximumDistance*_settings.MaximumDistance) return;
            var format=_settings.DecimalPlaces==0 ? "0" : _settings.DecimalPlaces==1 ? "0.0" : "0.00";
            var text=Mathf.Clamp(amount,0,9999999).ToString(format,CultureInfo.InvariantCulture);
            _popups[_cursor]=new Popup {Position=(float3)position+new float3(0,.12f,0),Lifetime=_settings.Lifetime,
                Text=new FixedString32Bytes(text)};
            _cursor=(_cursor+1)%_popups.Length;
            _liveFrames=1;
        }
        private void BindNetwork()
        {
            var manager=NetworkManager.Singleton;
            if(_network==manager && _messages!=null && manager != null && manager.IsListening) return;
            _messages?.UnregisterNamedMessageHandler(Channel);_messages=null;_network=manager;
            if(manager==null || !manager.IsListening) return;
            _messages=manager.CustomMessagingManager;_messages.RegisterNamedMessageHandler(Channel,Receive);
        }
        private void Receive(ulong sender,FastBufferReader reader)
        {
            if(_network==null || _network.IsServer || sender!=NetworkManager.ServerClientId) return;
            reader.ReadValueSafe(out int count);
            if(count<0 || count>PacketSize) return;
            for(var i=0;i<count;i++)
            { reader.ReadValueSafe(out Vector3 position);reader.ReadValueSafe(out float amount); if(float.IsFinite(amount)) Add(position,amount); }
        }
        private void Flush()
        {
            if(_pendingCount==0) return;
            if(_network!=null && _network.IsServer && _messages!=null)
            {
                using var writer=new FastBufferWriter(4+PacketSize*16,Allocator.Temp);
                writer.WriteValueSafe(_pendingCount);
                for(var i=0;i<_pendingCount;i++)
                { writer.WriteValueSafe(_pending[i].Position);writer.WriteValueSafe(_pending[i].Amount); }
                foreach(var client in _network.ConnectedClientsIds)
                    if(client!=NetworkManager.ServerClientId) _messages.SendNamedMessage(Channel,client,writer,NetworkDelivery.Unreliable);
            }
            _pendingCount=0;
        }
        private void LateUpdate()
        {
            BindNetwork();Flush();
            if(_material==null || _liveFrames==0) return;
            var camera=Camera.main;if(camera==null) return;
            if(_fontDirty) RebuildGlyphs();
            new AnimateGlyphs { Popups=_popups,Glyphs=_glyphs,Vertices=_vertices,
                Right=camera.transform.right,Up=camera.transform.up,Delta=Time.deltaTime,
                Rise=_settings.RiseSpeed,Scale=_settings.WorldTextHeight/Mathf.Max(1,_settings.AtlasFontSize),
                FadeAt=_settings.FadeStartsAt,Color=(Vector4)_settings.TextColor }.Schedule(_popups.Length,32).Complete();
            var live=false;
            for(var i=0;i<_popups.Length;i++) if(_popups[i].Lifetime>0){live=true;break;}
            if(!live){_liveFrames=0;return;}
            _mesh.SetVertexBufferData(_vertices,0,0,_vertices.Length,0,
                MeshUpdateFlags.DontRecalculateBounds|MeshUpdateFlags.DontValidateIndices);
            _mesh.bounds=new Bounds(camera.transform.position,Vector3.one*_settings.MaximumDistance*2.5f);
            Graphics.DrawMesh(_mesh,Matrix4x4.identity,_material,0,camera,0,null,ShadowCastingMode.Off,false);
        }
        [BurstCompile]
        private struct AnimateGlyphs : IJobParallelFor
        {
            public NativeArray<Popup> Popups;
            [ReadOnly] public NativeArray<Glyph> Glyphs;
            [NativeDisableParallelForRestriction] public NativeArray<Vertex> Vertices;
            public float3 Right,Up; public float Delta,Rise,Scale,FadeAt; public float4 Color;
            public void Execute(int index)
            {
                var p=Popups[index];p.Age+=Delta;
                if(p.Age>=p.Lifetime) p.Lifetime=0;
                Popups[index]=p;
                float width=0;
                var count=math.min(Characters,p.Text.Length);
                for(var i=0;i<count;i++) width+=Glyphs[p.Text[i]].Advance;
                var x=-width*.5f;
                var opacity=p.Lifetime<=0?0:math.saturate((1-p.Age/p.Lifetime)/math.max(.001f,1-FadeAt));
                var color=new Color32((byte)(Color.x*255),(byte)(Color.y*255),(byte)(Color.z*255),(byte)(opacity*Color.w*255));
                var origin=p.Position+new float3(0,p.Age*Rise,0);
                for(var i=0;i<Characters;i++)
                {
                    var v=(index*Characters+i)*4;
                    if(i>=count || p.Lifetime<=0)
                    { for(var j=0;j<4;j++) Vertices[v+j]=default;continue; }
                    var g=Glyphs[p.Text[i]];
                    Vertices[v]=Make(origin,x+g.Min.x,g.Min.y,g.UV0,color);
                    Vertices[v+1]=Make(origin,x+g.Min.x,g.Max.y,g.UV1,color);
                    Vertices[v+2]=Make(origin,x+g.Max.x,g.Max.y,g.UV2,color);
                    Vertices[v+3]=Make(origin,x+g.Max.x,g.Min.y,g.UV3,color);
                    x+=g.Advance;
                }
            }
            private Vertex Make(float3 origin,float x,float y,float2 uv,Color32 color) =>
                new Vertex {Position=origin+(Right*x+Up*y)*Scale,UV=uv,Color=color};
        }
#if UNITY_EDITOR
        // Exercises the actual Burst job without a camera render or Play Mode.
        public static void CheckPoolAnimation()
        {
            var mesh = new Mesh();
            mesh.SetVertexBufferParams(4,
                new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
                new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.UNorm8,4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,2));
            var validLayout = mesh.GetVertexBufferStride(0) == UnsafeUtility.SizeOf<Vertex>() &&
                mesh.GetVertexAttributeOffset(VertexAttribute.Color) == 12 &&
                mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0) == 16;
            DestroyImmediate(mesh);
            if (!validLayout) throw new InvalidOperationException("Damage glyph vertex layout does not match its mesh.");
            using var popups = new NativeArray<Popup>(128, Allocator.TempJob);
            using var glyphs = new NativeArray<Glyph>(128, Allocator.TempJob);
            using var vertices = new NativeArray<Vertex>(128 * Characters * 4, Allocator.TempJob);
            var glyphData = glyphs;
            var popupData = popups;
            glyphData['1'] = new Glyph { Min = float2.zero, Max = new float2(1, 1), Advance = 1 };
            for (var i = 0; i < popups.Length; i++)
                popupData[i] = new Popup { Position = new float3(i, 0, 0), Lifetime = 1, Text = new FixedString32Bytes("11") };
            var job = new AnimateGlyphs { Popups = popups, Glyphs = glyphs, Vertices = vertices,
                Right = new float3(1,0,0), Up = new float3(0,1,0), Delta = .5f, Rise = 2, Scale = 1,
                FadeAt = 0, Color = new float4(1,1,1,1) };
            job.Schedule(popups.Length, 32).Complete();
            for (var i = 0; i < popups.Length; i++)
            {
                var vertex = vertices[i * Characters * 4];
                if (math.abs(vertex.Position.y - 1f) > .001f || vertex.Color.a < 126 || vertex.Color.a > 128)
                    throw new InvalidOperationException("Damage popup rise/fade job failed.");
            }
            job.Schedule(popups.Length, 32).Complete();
            for (var i = 0; i < popups.Length; i++)
                if (popups[i].Lifetime != 0 || vertices[i * Characters * 4].Color.a != 0)
                    throw new InvalidOperationException("Expired damage popup was not released.");
        }
#endif
        private void OnDestroy()
        {
            _messages?.UnregisterNamedMessageHandler(Channel);
            Font.textureRebuilt-=FontRebuilt;SceneManager.sceneLoaded-=SceneLoaded;
            if(_popups.IsCreated)_popups.Dispose();if(_glyphs.IsCreated)_glyphs.Dispose();if(_vertices.IsCreated)_vertices.Dispose();
            if(_mesh!=null)Destroy(_mesh);if(_material!=null)Destroy(_material);
            if(_instance==this)_instance=null;
        }
    }
}
