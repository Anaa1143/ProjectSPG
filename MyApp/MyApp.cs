using DX12GameProgramming.Enums;
using ProiectSPG.Structs;
using SharpDX;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Forms;
using Resource = SharpDX.Direct3D12.Resource;
using ShaderResourceViewDimension = SharpDX.Direct3D12.ShaderResourceViewDimension;

namespace ProiectSPG
{
    public enum RenderLayer
    {
        Opaque = 0,
        Sky = 1,
        Transparent = 2,
        AlphaTested = 3
    }

    // =========================================================================
    // STICKMAN – om simplu din boxuri/sfere
    // =========================================================================
    public class Stickman
    {
        public Vector3 Position;
        public float Angle;
        public float Speed;
        public float WalkCycle;
        public bool IsPlayerControlled;

        public float RandomAngle;
        public float RandomTimer;

        public RenderItem Head, Torso, ArmL, ArmR, LegL, LegR;

        public Stickman(Vector3 pos, float angle, float speed, bool isPlayer)
        {
            Position = pos;
            Angle = angle;
            Speed = speed;
            IsPlayerControlled = isPlayer;
            RandomAngle = angle;
            RandomTimer = 0;
        }
    }

    public class MyApp : D3DApp
    {
        private readonly IList<FrameResource> frameResources = new List<FrameResource>(NUMBER_OF_FRAME_RESOURCES);
        private readonly IList<AutoResetEvent> fenceEvents = new List<AutoResetEvent>(NUMBER_OF_FRAME_RESOURCES);
        private int currentFrameResourceIndex;

        private DescriptorHeap srvDescriptorHeap;
        private DescriptorHeap[] descriptorHeaps;
        private RootSignature rootSignature;

        private readonly IDictionary<string, ShaderBytecode> shaders = new Dictionary<string, ShaderBytecode>();
        private readonly IDictionary<string, PipelineState> pipelineStates = new Dictionary<string, PipelineState>();
        private readonly IDictionary<string, MeshGeometry> geometries = new Dictionary<string, MeshGeometry>();
        private readonly IDictionary<string, Material> materials = new Dictionary<string, Material>();
        private readonly IDictionary<string, Texture> textures = new Dictionary<string, Texture>();

        private InputLayoutDescription inputLayout;
        private readonly IList<RenderItem> allRenderItems = new List<RenderItem>();

        private readonly IDictionary<RenderLayer, IList<RenderItem>> renderItemLayers =
            new Dictionary<RenderLayer, IList<RenderItem>>
            {
                [RenderLayer.Opaque] = new List<RenderItem>(),
                [RenderLayer.Sky] = new List<RenderItem>(),
                [RenderLayer.AlphaTested] = new List<RenderItem>(),
                [RenderLayer.Transparent] = new List<RenderItem>()
            };

        private int skyTexHeapIndex;
        private PassConstants mainPassCB = PassConstants.Default;
        private readonly Camera camera = new Camera();
        private Point lastMousePosition;

        private readonly Random rng = new Random(42);

        // =========================================================================
        // MASINA JUCATOR
        // =========================================================================
        private Vector3 carPosition = new Vector3(-35.0f, 1.0f, -55.0f);
        private float carAngle = 0.0f;
        private float carSpeed = 20.0f;
        private float carRotSpeed = 2.0f;
        private float wheelSpin = 0.0f;
        private Vector3 oldCarPos;

        private RenderItem carBodyRI, carRoofRI, carHoodRI;
        private RenderItem wheelFLRI, wheelFRRI, wheelRLRI, wheelRRRI;

        // =========================================================================
        // MASINA AI – merge automat pe circuit
        // =========================================================================
        private static readonly Vector3[] aiWaypoints = new Vector3[]
        {
            new Vector3(-35f, 1f, -55f),
            new Vector3(-35f, 1f,  55f),
            new Vector3( 35f, 1f,  55f),
            new Vector3( 35f, 1f, -55f),
        };
        private int aiWaypointIndex = 0;
        private Vector3 aiCarPosition = new Vector3(-35f, 1f, 20f);
        private float aiCarAngle = 0.0f;
        private float aiWheelSpin = 0.0f;
        private float aiCarSpeed = 14.0f;

        private RenderItem aiBodyRI, aiRoofRI, aiHoodRI;
        private RenderItem aiWheelFLRI, aiWheelFRRI, aiWheelRLRI, aiWheelRRRI;

        // =========================================================================
        // STICKMEN
        // =========================================================================
        private readonly List<Stickman> stickmen = new List<Stickman>();

        // =========================================================================
        // SEMAFOARE
        // =========================================================================
        private struct TrafficLight
        {
            public Vector3 Position;
            public float Timer;
            public int Phase;
            public RenderItem PostRI, RedRI, YellowRI, GreenRI;
        }
        private readonly List<TrafficLight> trafficLights = new List<TrafficLight>();

        // =========================================================================
        // COLIZIUNI
        // =========================================================================
        private readonly List<(Vector3 center, float radius)> colliders = new List<(Vector3, float)>();

        // Helper: decalajul sinusoidal al raului la un Z dat
        private static float RiverSinOffset(float z) => (float)Math.Sin(z * 0.045f) * 7.0f;

        public MyApp() { MainWindowCaption = "Oras Demo"; }

        private FrameResource CurrentFrameResource => frameResources[currentFrameResourceIndex];
        private AutoResetEvent CurrentFenceEvent => fenceEvents[currentFrameResourceIndex];

        // =========================================================================
        public override void Initialize()
        {
            base.Initialize();
            CommandList.Reset(DirectCmdListAlloc, null);
            camera.Position = new Vector3(0.0f, 35.0f, -130.0f);

            LoadTextures();
            CreateRootSignature();
            CreateDescriptorHeaps();
            CreateShadersAndInputLayout();
            CreateShapeGeometries();
            CreateMaterials();
            CreateRenderItems();
            CreateFrameResources();
            CreatePipelineStateObjects();

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            FlushCommandQueue();
        }

        protected override void OnResize()
        {
            base.OnResize();
            camera.SetLens(MathUtil.PiOverFour, AspectRatio, 1.0f, 1000.0f);
        }

        // =========================================================================
        protected override void Update(GameTimer gameTimer)
        {
            OnKeyboardInput(gameTimer);

            currentFrameResourceIndex = (currentFrameResourceIndex + 1) % NUMBER_OF_FRAME_RESOURCES;
            if (CurrentFrameResource.Fence != 0 && Fence.CompletedValue < CurrentFrameResource.Fence)
            {
                Fence.SetEventOnCompletion(CurrentFrameResource.Fence,
                    CurrentFenceEvent.SafeWaitHandle.DangerousGetHandle());
                CurrentFenceEvent.WaitOne();
            }

            float dt = gameTimer.DeltaTime;

            UpdateBillboards();
            UpdateTrafficLights(dt);
            UpdateStickmen(gameTimer);
            UpdateAICar(dt);
            UpdateObjectCBs();
            UpdateMaterialBuffer();
            UpdateMainPassCB(gameTimer);

            ApplyCarTransforms();

            // Coliziuni – sarim colizorul raului daca suntem pe pod (Z in [-7, +7])
            foreach (var col in colliders)
            {
                // Colizorul raului (centru la X=70, radius 12) e ignorat pe zona podului
                if (col.center.X > 65f && col.radius > 10f && Math.Abs(carPosition.Z) < 8.0f)
                    continue;

                float dx = Math.Abs(carPosition.X - col.center.X);
                float dz = Math.Abs(carPosition.Z - col.center.Z);
                float carR = 3.0f;
                if (dx < (carR + col.radius) && dz < (carR + col.radius))
                {
                    carPosition = oldCarPos;
                    ApplyCarTransforms();
                    break;
                }
            }

            float time = (float)gameTimer.TotalTime;
            foreach (var ri in allRenderItems)
            {
                if (ri.Material != null && ri.Material.Name == "water")
                {
                    ri.TexTransform = Matrix.Translation(0.0f, time * 0.025f, 0.0f);
                    ri.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                }
            }
        }

        // =========================================================================
        // KEYBOARD
        // =========================================================================
        private void OnKeyboardInput(GameTimer gt)
        {
            float dt = gt.DeltaTime;

            if (IsKeyDown(Keys.W) || IsKeyDown(Keys.Up)) camera.Walk(12.0f * dt);
            if (IsKeyDown(Keys.S) || IsKeyDown(Keys.Down)) camera.Walk(-12.0f * dt);
            if (IsKeyDown(Keys.A) || IsKeyDown(Keys.Left)) camera.Strafe(-12.0f * dt);
            if (IsKeyDown(Keys.D) || IsKeyDown(Keys.Right)) camera.Strafe(12.0f * dt);
            if (IsKeyDown(Keys.R)) camera.Position = new Vector3(0.0f, 35.0f, -130.0f);
            camera.UpdateViewMatrix();

            oldCarPos = carPosition;
            if (IsKeyDown(Keys.J)) carAngle -= carRotSpeed * dt;
            if (IsKeyDown(Keys.L)) carAngle += carRotSpeed * dt;
            Vector3 fwd = new Vector3((float)Math.Sin(carAngle), 0f, (float)Math.Cos(carAngle));
            if (IsKeyDown(Keys.I)) { carPosition += fwd * carSpeed * dt; wheelSpin -= carSpeed * dt * 1.5f; }
            if (IsKeyDown(Keys.K)) { carPosition -= fwd * carSpeed * dt; wheelSpin += carSpeed * dt * 1.5f; }

            Stickman player = stickmen.FirstOrDefault(s => s.IsPlayerControlled);
            if (player != null)
            {
                float pSpeed = 5.0f;
                bool moving = false;
                if (IsKeyDown(Keys.NumPad8) || IsKeyDown(Keys.G))
                {
                    Vector3 pf = new Vector3((float)Math.Sin(player.Angle), 0f, (float)Math.Cos(player.Angle));
                    player.Position += pf * pSpeed * dt;
                    player.WalkCycle += dt * 8f;
                    moving = true;
                }
                if (IsKeyDown(Keys.NumPad2) || IsKeyDown(Keys.H))
                {
                    Vector3 pf = new Vector3((float)Math.Sin(player.Angle), 0f, (float)Math.Cos(player.Angle));
                    player.Position -= pf * pSpeed * dt;
                    player.WalkCycle += dt * 8f;
                    moving = true;
                }
                if (IsKeyDown(Keys.NumPad4) || IsKeyDown(Keys.B)) { player.Angle -= 2.5f * dt; moving = true; }
                if (IsKeyDown(Keys.NumPad6) || IsKeyDown(Keys.N)) { player.Angle += 2.5f * dt; moving = true; }
                if (!moving && player.WalkCycle != 0) player.WalkCycle = 0;
            }
        }

        // =========================================================================
        // APLICA TRANSFORMARILE MASINII JUCATOR
        // =========================================================================
        private void ApplyCarTransforms()
        {
            Matrix rot = Matrix.RotationY(carAngle);
            Vector3 W(Vector3 local) => carPosition + Vector3.TransformCoordinate(local, rot);

            carBodyRI.World = Matrix.Scaling(4.0f, 1.0f, 8.0f) * rot * Matrix.Translation(carPosition);
            carBodyRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 roofPos = W(new Vector3(0f, 1.0f, -0.5f));
            carRoofRI.World = Matrix.Scaling(3.2f, 0.9f, 4.5f) * rot * Matrix.Translation(roofPos);
            carRoofRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 hoodPos = W(new Vector3(0f, 0.3f, 3.2f));
            carHoodRI.World = Matrix.Scaling(3.6f, 0.4f, 1.8f) * rot * Matrix.Translation(hoodPos);
            carHoodRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3[] wOff = {
                new Vector3(-2.2f, -0.7f,  2.8f), new Vector3( 2.2f, -0.7f,  2.8f),
                new Vector3(-2.2f, -0.7f, -2.8f), new Vector3( 2.2f, -0.7f, -2.8f),
            };
            RenderItem[] wh = { wheelFLRI, wheelFRRI, wheelRLRI, wheelRRRI };
            Matrix spin = Matrix.RotationX(wheelSpin);
            for (int i = 0; i < 4; i++)
            {
                Vector3 wp = W(wOff[i]);
                wh[i].World = Matrix.Scaling(1.1f, 1.1f, 0.7f) * spin * rot * Matrix.Translation(wp);
                wh[i].NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // MASINA AI – navigare pe waypoints
        // =========================================================================
        private void UpdateAICar(float dt)
        {
            Vector3 target = aiWaypoints[aiWaypointIndex];
            Vector3 toTarget = target - aiCarPosition;
            toTarget.Y = 0;
            float dist = toTarget.Length();

            if (dist < 4.0f)
            {
                aiWaypointIndex = (aiWaypointIndex + 1) % aiWaypoints.Length;
                target = aiWaypoints[aiWaypointIndex];
                toTarget = target - aiCarPosition;
                toTarget.Y = 0;
            }

            float targetAngle = (float)Math.Atan2(toTarget.X, toTarget.Z);
            float angleDiff = targetAngle - aiCarAngle;
            while (angleDiff > (float)Math.PI) angleDiff -= (float)(Math.PI * 2);
            while (angleDiff < -(float)Math.PI) angleDiff += (float)(Math.PI * 2);
            aiCarAngle += angleDiff * Math.Min(dt * 4.0f, 1.0f);

            Vector3 aiFwd = new Vector3((float)Math.Sin(aiCarAngle), 0f, (float)Math.Cos(aiCarAngle));
            aiCarPosition += aiFwd * aiCarSpeed * dt;
            aiCarPosition.Y = 1.0f;
            aiWheelSpin -= aiCarSpeed * dt * 1.5f;

            ApplyAICarTransforms();
        }

        private void ApplyAICarTransforms()
        {
            Matrix rot = Matrix.RotationY(aiCarAngle);
            Vector3 W(Vector3 local) => aiCarPosition + Vector3.TransformCoordinate(local, rot);

            aiBodyRI.World = Matrix.Scaling(4.0f, 1.0f, 8.0f) * rot * Matrix.Translation(aiCarPosition);
            aiBodyRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 roofPos = W(new Vector3(0f, 1.0f, -0.5f));
            aiRoofRI.World = Matrix.Scaling(3.2f, 0.9f, 4.5f) * rot * Matrix.Translation(roofPos);
            aiRoofRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3 hoodPos = W(new Vector3(0f, 0.3f, 3.2f));
            aiHoodRI.World = Matrix.Scaling(3.6f, 0.4f, 1.8f) * rot * Matrix.Translation(hoodPos);
            aiHoodRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

            Vector3[] wOff = {
                new Vector3(-2.2f, -0.7f,  2.8f), new Vector3( 2.2f, -0.7f,  2.8f),
                new Vector3(-2.2f, -0.7f, -2.8f), new Vector3( 2.2f, -0.7f, -2.8f),
            };
            RenderItem[] wh = { aiWheelFLRI, aiWheelFRRI, aiWheelRLRI, aiWheelRRRI };
            Matrix spin = Matrix.RotationX(aiWheelSpin);
            for (int i = 0; i < 4; i++)
            {
                Vector3 wp = W(wOff[i]);
                wh[i].World = Matrix.Scaling(1.1f, 1.1f, 0.7f) * spin * rot * Matrix.Translation(wp);
                wh[i].NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // SEMAFOARE
        // =========================================================================
        private void UpdateTrafficLights(float dt)
        {
            float[] phaseDurations = { 5.0f, 1.0f, 5.0f };
            for (int i = 0; i < trafficLights.Count; i++)
            {
                var tl = trafficLights[i];
                tl.Timer += dt;
                if (tl.Timer >= phaseDurations[tl.Phase])
                {
                    tl.Timer = 0;
                    tl.Phase = (tl.Phase + 1) % 3;
                }

                tl.GreenRI.Material = tl.Phase == 0 ? materials["semGreen"] : materials["semOff"];
                tl.YellowRI.Material = tl.Phase == 1 ? materials["semYellow"] : materials["semOff"];
                tl.RedRI.Material = tl.Phase == 2 ? materials["semRed"] : materials["semOff"];
                tl.GreenRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                tl.YellowRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                tl.RedRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                trafficLights[i] = tl;
            }
        }

        // =========================================================================
        // STICKMEN
        // =========================================================================
        private void UpdateStickmen(GameTimer gt)
        {
            float dt = gt.DeltaTime;
            float mapLimit = 90.0f;

            foreach (var sm in stickmen)
            {
                if (!sm.IsPlayerControlled)
                {
                    sm.RandomTimer -= dt;
                    if (sm.RandomTimer <= 0)
                    {
                        sm.RandomAngle = (float)(rng.NextDouble() * Math.PI * 2.0);
                        sm.RandomTimer = 2.0f + (float)rng.NextDouble() * 4.0f;
                    }
                    float angleDiff = sm.RandomAngle - sm.Angle;
                    while (angleDiff > (float)Math.PI) angleDiff -= (float)(Math.PI * 2);
                    while (angleDiff < -(float)Math.PI) angleDiff += (float)(Math.PI * 2);
                    sm.Angle += angleDiff * dt * 3.0f;

                    Vector3 dir = new Vector3((float)Math.Sin(sm.Angle), 0f, (float)Math.Cos(sm.Angle));
                    sm.Position += dir * sm.Speed * dt;
                    sm.WalkCycle += dt * sm.Speed * 1.5f;

                    if (Math.Abs(sm.Position.X) > mapLimit || Math.Abs(sm.Position.Z) > mapLimit)
                    {
                        sm.RandomAngle = (float)Math.Atan2(-sm.Position.X, -sm.Position.Z);
                        sm.RandomTimer = 1.0f;
                    }
                    // Nu intra in rau (rau la X=+70 centru, lat ~24)
                    if (sm.Position.X > 50.0f && !(sm.Position.X > 35.0f && sm.Position.X < 80.0f && Math.Abs(sm.Position.Z) < 6.0f))
                    {
                        sm.Position.X = 50.0f;
                        sm.RandomAngle = (float)Math.Atan2(-1, 0);
                    }
                }

                float wc = sm.WalkCycle;
                float legSwing = (float)Math.Sin(wc) * 0.5f;
                float armSwing = -(float)Math.Sin(wc) * 0.4f;

                Matrix smRot = Matrix.RotationY(sm.Angle);
                Vector3 P = sm.Position;
                Vector3 WL(Vector3 local) => P + Vector3.TransformCoordinate(local, smRot);

                Vector3 headPos = WL(new Vector3(0, 1.9f, 0));
                sm.Head.World = Matrix.Scaling(0.35f, 0.35f, 0.35f) * Matrix.Translation(headPos);
                sm.Head.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 torsoPos = WL(new Vector3(0, 1.15f, 0));
                sm.Torso.World = Matrix.Scaling(0.28f, 0.7f, 0.18f) * smRot * Matrix.Translation(torsoPos);
                sm.Torso.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 armLBase = WL(new Vector3(-0.22f, 1.45f, 0));
                Matrix armLRot = Matrix.RotationX(armSwing) * smRot;
                sm.ArmL.World = Matrix.Scaling(0.12f, 0.55f, 0.12f) * armLRot * Matrix.Translation(armLBase);
                sm.ArmL.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 armRBase = WL(new Vector3(0.22f, 1.45f, 0));
                Matrix armRRot = Matrix.RotationX(-armSwing) * smRot;
                sm.ArmR.World = Matrix.Scaling(0.12f, 0.55f, 0.12f) * armRRot * Matrix.Translation(armRBase);
                sm.ArmR.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 legLBase = WL(new Vector3(-0.12f, 0.7f, 0));
                Matrix legLRot = Matrix.RotationX(legSwing) * smRot;
                sm.LegL.World = Matrix.Scaling(0.14f, 0.65f, 0.14f) * legLRot * Matrix.Translation(legLBase);
                sm.LegL.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                Vector3 legRBase = WL(new Vector3(0.12f, 0.7f, 0));
                Matrix legRRot = Matrix.RotationX(-legSwing) * smRot;
                sm.LegR.World = Matrix.Scaling(0.14f, 0.65f, 0.14f) * legRRot * Matrix.Translation(legRBase);
                sm.LegR.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
            }
        }

        // =========================================================================
        // BILLBOARD UPDATE
        // =========================================================================
        private void UpdateBillboards()
        {
            foreach (var ri in renderItemLayers[RenderLayer.Transparent])
            {
                if (ri.Material.Name == "tree" || ri.Material.Name == "tree2")
                {
                    Vector3 pos = new Vector3(ri.World.M41, ri.World.M42, ri.World.M43);
                    Vector3 toCam = camera.Position - pos;
                    toCam.Y = 0; toCam.Normalize();
                    float angle = (float)Math.Atan2(toCam.X, toCam.Z);
                    ri.World = Matrix.Scaling(10.0f, 10.0f, 10.0f)
                             * Matrix.RotationY(angle)
                             * Matrix.Translation(pos);
                    ri.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;
                }
            }
        }

        // =========================================================================
        protected override void Draw(GameTimer gameTimer)
        {
            CommandAllocator cmdListAlloc = CurrentFrameResource.CommandAllocator;
            cmdListAlloc.Reset();
            CommandList.Reset(cmdListAlloc, pipelineStates["opaque"]);
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);
            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.LightSteelBlue);
            CommandList.ClearDepthStencilView(DepthStencilView, ClearFlags.FlagsDepth | ClearFlags.FlagsStencil, 1.0f, 0);
            CommandList.SetRenderTargets(CurrentBackBufferView, DepthStencilView);
            CommandList.SetDescriptorHeaps(descriptorHeaps.Length, descriptorHeaps);
            CommandList.SetGraphicsRootSignature(rootSignature);
            CommandList.SetGraphicsRootConstantBufferView(1, CurrentFrameResource.PassCB.Resource.GPUVirtualAddress);
            CommandList.SetGraphicsRootShaderResourceView(2, CurrentFrameResource.MaterialBuffer.Resource.GPUVirtualAddress);
            GpuDescriptorHandle skyTex = srvDescriptorHeap.GPUDescriptorHandleForHeapStart;
            skyTex += skyTexHeapIndex * CbvSrvUavDescriptorSize;
            CommandList.SetGraphicsRootDescriptorTable(3, skyTex);
            CommandList.SetGraphicsRootDescriptorTable(4, srvDescriptorHeap.GPUDescriptorHandleForHeapStart);

            CommandList.PipelineState = pipelineStates["opaque"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Opaque]);
            CommandList.PipelineState = pipelineStates["transparent"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Transparent]);
            CommandList.PipelineState = pipelineStates["sky"];
            DrawRenderItems(CommandList, renderItemLayers[RenderLayer.Sky]);

            CommandList.ResourceBarrierTransition(CurrentBackBuffer, ResourceStates.RenderTarget, ResourceStates.Present);
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            SwapChain.Present(0, PresentFlags.None);
            CurrentFrameResource.Fence = ++CurrentFence;
            CommandQueue.Signal(Fence, CurrentFence);
        }

        protected override void OnMouseDown(MouseButtons button, Point location)
        {
            base.OnMouseDown(button, location);
            lastMousePosition = location;
        }

        protected override void OnMouseMove(MouseButtons button, Point location)
        {
            if ((button & MouseButtons.Left) != 0)
            {
                float dx = MathUtil.DegreesToRadians(0.25f * (location.X - lastMousePosition.X));
                float dy = MathUtil.DegreesToRadians(0.25f * (location.Y - lastMousePosition.Y));
                camera.Pitch(dy);
                camera.RotateY(dx);
            }
            lastMousePosition = location;
        }

        protected override void Dispose(bool isDisposing)
        {
            if (isDisposing)
            {
                rootSignature.Dispose();
                srvDescriptorHeap?.Dispose();
                foreach (Texture t in textures.Values) t.Dispose();
                foreach (FrameResource f in frameResources) f.Dispose();
                foreach (MeshGeometry g in geometries.Values) g.Dispose();
                foreach (PipelineState p in pipelineStates.Values) p.Dispose();
            }
            base.Dispose(isDisposing);
        }

        // =========================================================================
        private void UpdateObjectCBs()
        {
            foreach (RenderItem ri in allRenderItems)
            {
                if (ri.NumFramesDirty > 0)
                {
                    var obj = new ObjectConstants
                    {
                        World = Matrix.Transpose(ri.World),
                        TexTransform = Matrix.Transpose(ri.TexTransform),
                        MaterialIndex = ri.Material.MaterialConstantBufferIndex
                    };
                    CurrentFrameResource.ObjectCB.CopyData(ri.ObjCBIndex, ref obj);
                    ri.NumFramesDirty--;
                }
            }
        }

        private void UpdateMaterialBuffer()
        {
            var cb = CurrentFrameResource.MaterialBuffer;
            foreach (Material mat in materials.Values)
            {
                if (mat.NumberOfFramesDirty > 0)
                {
                    var md = new MaterialData
                    {
                        DiffuseAlbedo = mat.DiffuseAlbedo,
                        FresnelR0 = mat.FresnelR0,
                        Roughness = mat.Roughness,
                        MatTransform = Matrix.Transpose(mat.MatTransform),
                        DiffuseMapIndex = mat.DiffuseSrvHeapIndex
                    };
                    cb.CopyData(mat.MaterialConstantBufferIndex, ref md);
                    mat.NumberOfFramesDirty--;
                }
            }
        }

        private void UpdateMainPassCB(GameTimer gt)
        {
            Matrix view = camera.View;
            Matrix proj = camera.Proj;
            Matrix viewProj = view * proj;

            mainPassCB.View = Matrix.Transpose(view);
            mainPassCB.InvView = Matrix.Transpose(Matrix.Invert(view));
            mainPassCB.Proj = Matrix.Transpose(proj);
            mainPassCB.InvProj = Matrix.Transpose(Matrix.Invert(proj));
            mainPassCB.ViewProj = Matrix.Transpose(viewProj);
            mainPassCB.InvViewProj = Matrix.Transpose(Matrix.Invert(viewProj));
            mainPassCB.EyePosW = camera.Position;
            mainPassCB.RenderTargetSize = new Vector2(WindowWidth, WindowHeight);
            mainPassCB.InvRenderTargetSize = 1.0f / mainPassCB.RenderTargetSize;
            mainPassCB.NearZ = 1.0f;
            mainPassCB.FarZ = 1000.0f;
            mainPassCB.TotalTime = gt.TotalTime;
            mainPassCB.DeltaTime = gt.DeltaTime;

            mainPassCB.AmbientLight = new Vector4(0.28f, 0.28f, 0.38f, 1.0f);
            mainPassCB.Lights[0].Direction = new Vector3(0.57735f, -0.57735f, 0.57735f);
            mainPassCB.Lights[0].Strength = new Vector3(0.65f);
            mainPassCB.Lights[1].Direction = new Vector3(-0.57735f, -0.57735f, 0.57735f);
            mainPassCB.Lights[1].Strength = new Vector3(0.3f);
            mainPassCB.Lights[2].Direction = new Vector3(0.0f, -0.707f, -0.707f);
            mainPassCB.Lights[2].Strength = new Vector3(0.15f);

            if (CurrentFrameResource == null) return;
            if (mainPassCB.Lights == null) mainPassCB.Lights = Light.DefaultArray;

            int lightIndex = 3;
            float flicker = 0.88f + 0.24f * (float)Math.Sin(gt.TotalTime * 4.5f);
            float[] lampZ = { -52.5f, -37.5f, -22.5f, -7.5f, 7.5f, 22.5f, 37.5f, 52.5f };

            foreach (float fz in lampZ)
            {
                if (lightIndex >= 19) break;
                mainPassCB.Lights[lightIndex].Position = new Vector3(-44.0f, 7.5f, fz);
                mainPassCB.Lights[lightIndex].Strength = new Vector3(3.2f, 2.4f, 0.7f) * flicker;
                mainPassCB.Lights[lightIndex].FalloffStart = 1.0f;
                mainPassCB.Lights[lightIndex].FalloffEnd = 24.0f;
                lightIndex++;
            }
            foreach (float fz in lampZ)
            {
                if (lightIndex >= 19) break;
                mainPassCB.Lights[lightIndex].Position = new Vector3(44.0f, 7.5f, fz);
                mainPassCB.Lights[lightIndex].Strength = new Vector3(3.2f, 2.4f, 0.7f) * flicker;
                mainPassCB.Lights[lightIndex].FalloffStart = 1.0f;
                mainPassCB.Lights[lightIndex].FalloffEnd = 24.0f;
                lightIndex++;
            }

            CurrentFrameResource.PassCB.CopyData(0, ref mainPassCB);
        }

        // =========================================================================
        private void LoadTextures()
        {
            AddTexture("grass", "grass.dds");
            AddTexture("water", "water.dds");
            AddTexture("skyCubeMap", "grasscube1024.dds");
            AddTexture("street", "street.dds");
            AddTexture("tree", "tree02S.dds");
            AddTexture("house", "house1.dds");
            AddTexture("house2", "house2.dds");
            AddTexture("house3", "house3.dds");
            AddTexture("tree2", "treeArray2.dds");
            AddTexture("pavement", "pavement.dds");
        }

        private void AddTexture(string name, string fileName)
        {
            var tex = new Texture { Name = name, FileName = $"Textures\\{fileName}" };
            tex.Resource = TextureUtilities.CreateTextureFromDDS(Device, tex.FileName);
            textures[tex.Name] = tex;
        }

        private void CreateRootSignature()
        {
            var slots = new[]
            {
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(1, 0), RootParameterType.ConstantBufferView),
                new RootParameter(ShaderVisibility.All, new RootDescriptor(0, 1), RootParameterType.ShaderResourceView),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)),
                new RootParameter(ShaderVisibility.All, new DescriptorRange(DescriptorRangeType.ShaderResourceView, 6, 1))
            };
            rootSignature = Device.CreateRootSignature(
                new RootSignatureDescription(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    slots, GetStaticSamplers()).Serialize());
        }

        private void CreateDescriptorHeaps()
        {
            srvDescriptorHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                DescriptorCount = 20,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            });
            descriptorHeaps = new[] { srvDescriptorHeap };

            CpuDescriptorHandle cpu = srvDescriptorHeap.CPUDescriptorHandleForHeapStart;
            Resource[] tex2D = {
                textures["grass"].Resource,   textures["water"].Resource,
                textures["street"].Resource,  textures["tree"].Resource,
                textures["house"].Resource,   textures["house2"].Resource,
                textures["house3"].Resource,  textures["tree2"].Resource,
                textures["pavement"].Resource
            };
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DHelper.DefaultShader4ComponentMapping,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new ShaderResourceViewDescription.Texture2DResource
                { MostDetailedMip = 0, ResourceMinLODClamp = 0.0f }
            };
            foreach (var t in tex2D)
            {
                srvDesc.Format = t.Description.Format;
                srvDesc.Texture2D.MipLevels = t.Description.MipLevels;
                Device.CreateShaderResourceView(t, srvDesc, cpu);
                cpu += CbvSrvUavDescriptorSize;
            }
            Resource skyT = textures["skyCubeMap"].Resource;
            srvDesc.Dimension = ShaderResourceViewDimension.TextureCube;
            srvDesc.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
            { MostDetailedMip = 0, MipLevels = skyT.Description.MipLevels, ResourceMinLODClamp = 0.0f };
            srvDesc.Format = skyT.Description.Format;
            Device.CreateShaderResourceView(skyT, srvDesc, cpu);
            skyTexHeapIndex = 9;
        }

        private void CreateShadersAndInputLayout()
        {
            shaders["standardVS"] = D3DHelper.CompileShader("Shaders\\Default.hlsl", "VS", "vs_5_1");
            shaders["opaquePS"] = D3DHelper.CompileShader("Shaders\\Default.hlsl", "PS", "ps_5_1");
            shaders["skyVS"] = D3DHelper.CompileShader("Shaders\\Sky.hlsl", "VS", "vs_5_1");
            shaders["skyPS"] = D3DHelper.CompileShader("Shaders\\Sky.hlsl", "PS", "ps_5_1");
            inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float,  0, 0),
                new InputElement("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float,    24, 0)
            });
        }

        float GetHillHeight(float x, float z) => 0.0f;
        Vector3 GetHillNormal(float x, float z) => new Vector3(0, 1, 0);

        // =========================================================================
        private void CreateShapeGeometries()
        {
            var vertices = new List<Vertex>();
            var indices = new List<short>();

            var gridMesh = GeometryGenerator.CreateGrid(350.0f, 350.0f, 70, 70);

            var treeMesh = new GeometryGenerator.MeshData();
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(-0.5f, 0f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(0, 1) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(-0.5f, 1f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(0, 0) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(0.5f, 1f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(1, 0) });
            treeMesh.Vertices.Add(new GeometryGenerator.Vertex { Position = new Vector3(0.5f, 0f, 0f), Normal = new Vector3(0, 0, -1), TexC = new Vector2(1, 1) });
            treeMesh.Indices32.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
            SubmeshGeometry tree = AppendMeshData(treeMesh, vertices, indices);

            var boxMesh = GeometryGenerator.CreateBox(1.0f, 1.0f, 1.0f, 1);

            // RAU: centru X=+70, sinusoidal
            var riverMesh = GeometryGenerator.CreateGrid(24.0f, 310.0f, 32, 130);
            for (int i = 0; i < riverMesh.Vertices.Count; i++)
            {
                var v = riverMesh.Vertices[i]; var p = v.Position;
                p.X += RiverSinOffset(p.Z);
                p.Y = 0.05f;
                v.Position = p; v.Normal = GetHillNormal(p.X, p.Z);
                riverMesh.Vertices[i] = v;
            }

            SubmeshGeometry box = AppendMeshData(boxMesh, vertices, indices);
            SubmeshGeometry grid = AppendMeshData(gridMesh, vertices, indices);
            SubmeshGeometry sphere = AppendMeshData(GeometryGenerator.CreateSphere(0.5f, 20, 20), vertices, indices);
            SubmeshGeometry river = AppendMeshData(riverMesh, vertices, indices);

            var geo = MeshGeometry.New(Device, CommandList, vertices, indices.ToArray(), "shapeGeo");
            geo.DrawArguments["box"] = box;
            geo.DrawArguments["grid"] = grid;
            geo.DrawArguments["sphere"] = sphere;
            geo.DrawArguments["river"] = river;
            geo.DrawArguments["tree"] = tree;
            geometries[geo.Name] = geo;
        }

        private SubmeshGeometry AppendMeshData(GeometryGenerator.MeshData meshData,
            List<Vertex> vertices, List<short> indices)
        {
            var sub = new SubmeshGeometry
            {
                IndexCount = meshData.Indices32.Count,
                StartIndexLocation = indices.Count,
                BaseVertexLocation = vertices.Count
            };
            vertices.AddRange(meshData.Vertices.Select(v =>
                new Vertex { Pos = v.Position, Normal = v.Normal, TexC = v.TexC }));
            indices.AddRange(meshData.GetIndices16());
            return sub;
        }

        private void CreatePipelineStateObjects()
        {
            var opaquePSO = new GraphicsPipelineStateDescription
            {
                InputLayout = inputLayout,
                RootSignature = rootSignature,
                VertexShader = shaders["standardVS"],
                PixelShader = shaders["opaquePS"],
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = DepthStencilStateDescription.Default(),
                SampleMask = unchecked((int)uint.MaxValue),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality),
                DepthStencilFormat = DepthStencilFormat
            };
            opaquePSO.RenderTargetFormats[0] = BackBufferFormat;
            pipelineStates["opaque"] = Device.CreateGraphicsPipelineState(opaquePSO);

            var tPSO = opaquePSO.Copy();
            tPSO.RasterizerState.CullMode = CullMode.None;
            tPSO.BlendState.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                LogicOpEnable = false,
                SourceBlend = BlendOption.SourceAlpha,
                DestinationBlend = BlendOption.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One,
                DestinationAlphaBlend = BlendOption.Zero,
                AlphaBlendOperation = BlendOperation.Add,
                LogicOp = LogicOperation.Noop,
                RenderTargetWriteMask = ColorWriteMaskFlags.All
            };
            pipelineStates["transparent"] = Device.CreateGraphicsPipelineState(tPSO);

            var skyPSO = opaquePSO.Copy();
            skyPSO.RasterizerState.CullMode = CullMode.None;
            skyPSO.DepthStencilState.DepthComparison = Comparison.LessEqual;
            skyPSO.VertexShader = shaders["skyVS"];
            skyPSO.PixelShader = shaders["skyPS"];
            pipelineStates["sky"] = Device.CreateGraphicsPipelineState(skyPSO);
        }

        private void CreateFrameResources()
        {
            for (int i = 0; i < NUMBER_OF_FRAME_RESOURCES; i++)
            {
                frameResources.Add(new FrameResource(Device, 1, allRenderItems.Count, materials.Count));
                fenceEvents.Add(new AutoResetEvent(false));
            }
        }

        // =========================================================================
        // MATERIALE
        // =========================================================================
        private void CreateMaterials()
        {
            int i = 0;
            AddMaterial(new Material { Name = "grassMaterial", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 0, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.01f), Roughness = 1.0f });
            AddMaterial(new Material { Name = "sky", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 3, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.1f), Roughness = 1.0f });
            AddMaterial(new Material { Name = "water", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 1, DiffuseAlbedo = new Vector4(0.6f, 0.8f, 1.0f, 0.75f), FresnelR0 = new Vector3(0.2f), Roughness = 0.0f });
            AddMaterial(new Material { Name = "street", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.4f });
            AddMaterial(new Material { Name = "tree", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 3, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.8f });
            AddMaterial(new Material { Name = "house", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 4, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.3f });
            AddMaterial(new Material { Name = "house2", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 5, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.3f });
            AddMaterial(new Material { Name = "house3", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 6, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.3f });
            AddMaterial(new Material { Name = "tree2", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 7, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.8f });
            AddMaterial(new Material { Name = "pavement", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 8, DiffuseAlbedo = Vector4.One, FresnelR0 = new Vector3(0.02f), Roughness = 0.4f });
            AddMaterial(new Material { Name = "lampPost", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.06f, 0.06f, 0.06f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.8f });
            AddMaterial(new Material { Name = "lightBulb", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(5, 4, 2, 1), FresnelR0 = new Vector3(0.9f), Roughness = 0.0f });
            AddMaterial(new Material { Name = "carPaint", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.85f, 0.08f, 0.08f, 1), FresnelR0 = new Vector3(0.15f), Roughness = 0.05f });
            AddMaterial(new Material { Name = "carRoof", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.55f, 0.04f, 0.04f, 1), FresnelR0 = new Vector3(0.1f), Roughness = 0.1f });
            AddMaterial(new Material { Name = "carWheel", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.04f, 0.04f, 0.04f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.95f });
            AddMaterial(new Material { Name = "fence", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.55f, 0.40f, 0.25f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "crosswalk", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 8, DiffuseAlbedo = new Vector4(0.95f, 0.95f, 0.95f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.6f });
            AddMaterial(new Material { Name = "parking", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 8, DiffuseAlbedo = new Vector4(0.45f, 0.45f, 0.5f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.5f });
            AddMaterial(new Material { Name = "stickSkin", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.95f, 0.78f, 0.55f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.8f });
            AddMaterial(new Material { Name = "stickClothes", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.2f, 0.35f, 0.75f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "stickPlayer", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.1f, 0.75f, 0.1f, 1), FresnelR0 = new Vector3(0.05f), Roughness = 0.7f });
            AddMaterial(new Material { Name = "carParked1", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.1f, 0.2f, 0.8f, 1), FresnelR0 = new Vector3(0.15f), Roughness = 0.05f });
            AddMaterial(new Material { Name = "carParked2", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.8f, 0.7f, 0.05f, 1), FresnelR0 = new Vector3(0.15f), Roughness = 0.05f });
            AddMaterial(new Material { Name = "carParked3", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.85f, 0.85f, 0.85f, 1), FresnelR0 = new Vector3(0.2f), Roughness = 0.04f });
            AddMaterial(new Material { Name = "semPost", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.1f, 0.1f, 0.1f, 1), FresnelR0 = new Vector3(0.05f), Roughness = 0.8f });
            AddMaterial(new Material { Name = "semOff", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.12f, 0.12f, 0.12f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "semRed", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(5.0f, 0.05f, 0.05f, 1), FresnelR0 = new Vector3(0.9f), Roughness = 0.0f });
            AddMaterial(new Material { Name = "semYellow", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(5.0f, 4.5f, 0.05f, 1), FresnelR0 = new Vector3(0.9f), Roughness = 0.0f });
            AddMaterial(new Material { Name = "semGreen", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.05f, 5.0f, 0.05f, 1), FresnelR0 = new Vector3(0.9f), Roughness = 0.0f });
            // ACOPERIS NEGRU
            AddMaterial(new Material { Name = "roof", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.04f, 0.04f, 0.04f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.7f });
            // Masina AI (albastru inchis)
            AddMaterial(new Material { Name = "aiCarPaint", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.05f, 0.15f, 0.7f, 1), FresnelR0 = new Vector3(0.15f), Roughness = 0.05f });
            AddMaterial(new Material { Name = "aiCarRoof", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.03f, 0.10f, 0.5f, 1), FresnelR0 = new Vector3(0.1f), Roughness = 0.1f });
            // Haine colorate suplimentare pentru stickmen variati
            AddMaterial(new Material { Name = "stickRed", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.8f, 0.1f, 0.1f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "stickGreen", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.1f, 0.6f, 0.2f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "stickOrange", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.9f, 0.45f, 0.05f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            AddMaterial(new Material { Name = "stickPurple", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.5f, 0.1f, 0.7f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.9f });
            // Linie de mijloc drum (galben/alb)
            AddMaterial(new Material { Name = "roadLine", MaterialConstantBufferIndex = i++, DiffuseSrvHeapIndex = 2, DiffuseAlbedo = new Vector4(0.95f, 0.90f, 0.05f, 1), FresnelR0 = new Vector3(0.02f), Roughness = 0.5f });
        }

        private void AddMaterial(Material m) => materials[m.Name] = m;
        private void RegisterCollider(Vector3 center, float radius) => colliders.Add((center, radius));

        // =========================================================================
        // CREATE RENDER ITEMS
        // =========================================================================
        private void CreateRenderItems()
        {
            int idx = 0;
            MeshGeometry geo = geometries["shapeGeo"];

            // ─── MASINA JUCATOR ───────────────────────────────────────────────────
            carBodyRI = MakeBoxRI(geo, materials["carPaint"], idx++);
            carRoofRI = MakeBoxRI(geo, materials["carRoof"], idx++);
            carHoodRI = MakeBoxRI(geo, materials["carPaint"], idx++);
            wheelFLRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            wheelFRRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            wheelRLRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            wheelRRRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            foreach (var ri in new[] { carBodyRI, carRoofRI, carHoodRI, wheelFLRI, wheelFRRI, wheelRLRI, wheelRRRI })
            { renderItemLayers[RenderLayer.Opaque].Add(ri); allRenderItems.Add(ri); }
            ApplyCarTransforms();

            // ─── MASINA AI ────────────────────────────────────────────────────────
            aiBodyRI = MakeBoxRI(geo, materials["aiCarPaint"], idx++);
            aiRoofRI = MakeBoxRI(geo, materials["aiCarRoof"], idx++);
            aiHoodRI = MakeBoxRI(geo, materials["aiCarPaint"], idx++);
            aiWheelFLRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            aiWheelFRRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            aiWheelRLRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            aiWheelRRRI = MakeSphereRI(geo, materials["carWheel"], idx++);
            foreach (var ri in new[] { aiBodyRI, aiRoofRI, aiHoodRI, aiWheelFLRI, aiWheelFRRI, aiWheelRLRI, aiWheelRRRI })
            { renderItemLayers[RenderLayer.Opaque].Add(ri); allRenderItems.Add(ri); }
            ApplyAICarTransforms();

            // =========================================================================
            // CIRCUIT OVAL – FIX: tronsoanele verticale se opresc exact la intersectie
            // (Z: -55 .. +55) astfel incat sa nu depaseasca tronsoanele orizontale la Z=+/-60
            // Tronsoanele orizontale acopera de la X=-41 la X=+41 (lat 82)
            // Patrate de intersectie la colturi acopera zona comuna
            // =========================================================================

            // ─── TRONSOANE ORIZONTALE (Nord Z=-60, Sud Z=+60) – lat 10, lungime 82 ─
            AddRI(RenderLayer.Opaque, idx++, "street", "shapeGeo", "box",
                Matrix.Scaling(82f, 0.3f, 10f) * Matrix.Translation(0f, 0.15f, -60f));
            AddRI(RenderLayer.Opaque, idx++, "street", "shapeGeo", "box",
                Matrix.Scaling(82f, 0.3f, 10f) * Matrix.Translation(0f, 0.15f, 60f));

            // ─── TRONSOANE VERTICALE (Vest X=-35, Est X=+35) – SCURTATE la Z=-55..+55 ─
            // Lungime = 110 (de la -55 la +55), nu mai intra in intersectii
            AddRI(RenderLayer.Opaque, idx++, "street", "shapeGeo", "box",
                Matrix.Scaling(10f, 0.3f, 110f) * Matrix.Translation(-35f, 0.15f, 0f));
            AddRI(RenderLayer.Opaque, idx++, "street", "shapeGeo", "box",
                Matrix.Scaling(10f, 0.3f, 110f) * Matrix.Translation(35f, 0.15f, 0f));

            // ─── PATRATE DE INTERSECTIE la colturi (10x10) – un singur strat, fara suprapunere ─
            // Colturi: NV(-35,-60), NE(+35,-60), SV(-35,+60), SE(+35,+60)
            float[] cxArr = { -35f, 35f, -35f, 35f };
            float[] czArr = { -60f, -60f, 60f, 60f };
            for (int c = 0; c < 4; c++)
                AddRI(RenderLayer.Opaque, idx++, "street", "shapeGeo", "box",
                    Matrix.Scaling(10f, 0.32f, 10f) * Matrix.Translation(cxArr[c], 0.16f, czArr[c]));

            // ─── LINII DE MIJLOC PE TRONSOANE ORIZONTALE (Nord si Sud) ───────────
            // Dungi discontinue galbene pe axa X, la Z=+/-60, y putin deasupra drumului
            float lineY = 0.32f;
            // Nord (Z=-60): dungi de la X=-36 la X=+36 (evitam intersectiile X=+/-35+/-5)
            for (float lx = -33f; lx <= 33f; lx += 6.0f)
                AddRI(RenderLayer.Opaque, idx++, "roadLine", "shapeGeo", "box",
                    Matrix.Scaling(3.5f, 0.02f, 0.3f) * Matrix.Translation(lx, lineY, -60f));
            // Sud (Z=+60)
            for (float lx = -33f; lx <= 33f; lx += 6.0f)
                AddRI(RenderLayer.Opaque, idx++, "roadLine", "shapeGeo", "box",
                    Matrix.Scaling(3.5f, 0.02f, 0.3f) * Matrix.Translation(lx, lineY, 60f));

            // ─── LINII DE MIJLOC PE TRONSOANE VERTICALE (Vest si Est) ────────────
            // Dungi discontinue pe axa Z, la X=+/-35
            for (float lz = -50f; lz <= 50f; lz += 6.0f)
            {
                AddRI(RenderLayer.Opaque, idx++, "roadLine", "shapeGeo", "box",
                    Matrix.Scaling(0.3f, 0.02f, 3.5f) * Matrix.Translation(-35f, lineY, lz));
                AddRI(RenderLayer.Opaque, idx++, "roadLine", "shapeGeo", "box",
                    Matrix.Scaling(0.3f, 0.02f, 3.5f) * Matrix.Translation(35f, lineY, lz));
            }

            // ─── TROTUARE LANGA CIRCUIT ───────────────────────────────────────────
            // Nord/Sud – paralele cu tronsoanele orizontale
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(82f, 0.2f, 3f) * Matrix.Translation(0f, 0.1f, -54f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(82f, 0.2f, 3f) * Matrix.Translation(0f, 0.1f, -66f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(82f, 0.2f, 3f) * Matrix.Translation(0f, 0.1f, 54f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(82f, 0.2f, 3f) * Matrix.Translation(0f, 0.1f, 66f));
            // Vest/Est – paralele cu tronsoanele verticale, scurtate sa nu intre in intersectie
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(3f, 0.2f, 110f) * Matrix.Translation(-41f, 0.1f, 0f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(3f, 0.2f, 110f) * Matrix.Translation(-29f, 0.1f, 0f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(3f, 0.2f, 110f) * Matrix.Translation(41f, 0.1f, 0f));
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box", Matrix.Scaling(3f, 0.2f, 110f) * Matrix.Translation(29f, 0.1f, 0f));

            // ─── TRECERI DE PIETONI LA MIJLOCUL FIECARUI TRONSON ─────────────────
            // Helper local pentru treceri de pietoni (dungi albe)
            void AddCrosswalk(float cx, float cz, bool eastWest)
            {
                for (int d = -2; d <= 2; d++)
                {
                    float ox = eastWest ? 0f : d * 1.6f;
                    float oz = eastWest ? d * 1.6f : 0f;
                    Matrix scl = eastWest
                        ? Matrix.Scaling(1.2f, 0.32f, 10.5f)
                        : Matrix.Scaling(10.5f, 0.32f, 1.2f);
                    AddRI(RenderLayer.Opaque, idx++, "crosswalk", "shapeGeo", "box",
                        scl * Matrix.Translation(cx + ox, 0.0f, cz + oz));
                }
            }

            // Treceri la MIJLOCUL fiecarui tronson (Z=0 pe Vest/Est, X=0 pe Nord/Sud)
            AddCrosswalk(-35f, 0f, false);  // Vest, mijloc Z=0
            AddCrosswalk(35f, 0f, false);  // Est,  mijloc Z=0
            AddCrosswalk(0f, -60f, true);  // Nord, mijloc X=0
            AddCrosswalk(0f, 60f, true);  // Sud,  mijloc X=0

            // ─── SEMAFOARE LA MIJLOCUL LUNGIMII FIECARUI TRONSON ─────────────────
            // Vest: X=-35, Z=0 (mijlocul tronsonului lung)
            // Est:  X=+35, Z=0
            // Nord: X=0,   Z=-60
            // Sud:  X=0,   Z=+60
            var semPositions = new (float x, float z, bool isVertical)[]
            {
                (-35f,  0f,  true),   // Vest – tronson vertical, semafor la Z=0
                ( 35f,  0f,  true),   // Est
                (  0f, -60f, false),  // Nord – tronson orizontal, semafor la X=0
                (  0f,  60f, false),  // Sud
            };
            float[] semPhaseOffset = { 0f, 5.5f, 2.75f, 8.25f };

            for (int s = 0; s < semPositions.Length; s++)
            {
                var (spx, spz, isVert) = semPositions[s];
                // Offsetam stalpul lateral fata de drum
                float offX = isVert ? (spx < 0 ? -6f : 6f) : 0f;
                float offZ = !isVert ? (spz < 0 ? -6f : 6f) : 0f;
                float sx = spx + offX;
                float sz = spz + offZ;

                var tl = new TrafficLight
                {
                    Position = new Vector3(sx, 0, sz),
                    Timer = semPhaseOffset[s],
                    Phase = s % 3
                };

                // Stalp vertical
                AddRI(RenderLayer.Opaque, idx++, "semPost", "shapeGeo", "box",
                    Matrix.Scaling(0.35f, 6.0f, 0.35f) * Matrix.Translation(sx, 3.0f, sz));
                RegisterCollider(new Vector3(sx, 0, sz), 0.4f);
                // Cutia semaforului
                AddRI(RenderLayer.Opaque, idx++, "semPost", "shapeGeo", "box",
                    Matrix.Scaling(0.8f, 2.4f, 0.8f) * Matrix.Translation(sx, 7.2f, sz));

                tl.RedRI = MakeSphereRI(geo, materials["semOff"], idx++);
                tl.YellowRI = MakeSphereRI(geo, materials["semOff"], idx++);
                tl.GreenRI = MakeSphereRI(geo, materials["semOff"], idx++);

                float faceOff = isVert ? 0.41f : 0.41f;
                tl.RedRI.World = Matrix.Scaling(0.45f, 0.45f, 0.45f) * Matrix.Translation(sx, 8.5f, sz - faceOff);
                tl.YellowRI.World = Matrix.Scaling(0.45f, 0.45f, 0.45f) * Matrix.Translation(sx, 7.7f, sz - faceOff);
                tl.GreenRI.World = Matrix.Scaling(0.45f, 0.45f, 0.45f) * Matrix.Translation(sx, 6.9f, sz - faceOff);
                tl.RedRI.NumFramesDirty = tl.YellowRI.NumFramesDirty = tl.GreenRI.NumFramesDirty = NUMBER_OF_FRAME_RESOURCES;

                renderItemLayers[RenderLayer.Opaque].Add(tl.RedRI);
                renderItemLayers[RenderLayer.Opaque].Add(tl.YellowRI);
                renderItemLayers[RenderLayer.Opaque].Add(tl.GreenRI);
                allRenderItems.Add(tl.RedRI);
                allRenderItems.Add(tl.YellowRI);
                allRenderItems.Add(tl.GreenRI);
                trafficLights.Add(tl);
            }

            // ─── 16 FELINARE (8 Vest X=-44, 8 Est X=+44) ────────────────────────
            float[] lampZ = { -52.5f, -37.5f, -22.5f, -7.5f, 7.5f, 22.5f, 37.5f, 52.5f };
            foreach (float lz in lampZ)
            {
                AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                    Matrix.Scaling(0.3f, 7.0f, 0.3f) * Matrix.Translation(-44f, 3.5f, lz));
                AddRI(RenderLayer.Opaque, idx++, "lightBulb", "shapeGeo", "sphere",
                    Matrix.Scaling(1.3f, 1.3f, 1.3f) * Matrix.Translation(-44f, 8.0f, lz));
                RegisterCollider(new Vector3(-44f, 0, lz), 0.5f);

                AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                    Matrix.Scaling(0.3f, 7.0f, 0.3f) * Matrix.Translation(44f, 3.5f, lz));
                AddRI(RenderLayer.Opaque, idx++, "lightBulb", "shapeGeo", "sphere",
                    Matrix.Scaling(1.3f, 1.3f, 1.3f) * Matrix.Translation(44f, 8.0f, lz));
                RegisterCollider(new Vector3(44f, 0, lz), 0.5f);
            }

            // ─── PARCARE (langa tronsonul Nord, exterior, Z=-76...-90) ───────────
            AddRI(RenderLayer.Opaque, idx++, "parking", "shapeGeo", "box",
                Matrix.Scaling(36f, 0.25f, 18f) * Matrix.Translation(-10f, 0.12f, -80f));

            string[] carMats = { "carParked1", "carParked2", "carParked3" };
            for (int c = 0; c < 6; c++)
            {
                float px = -24f + (c % 3) * 11f;
                float pz = -76f - (c / 3) * 8.5f;
                string cm = carMats[c % 3];
                AddRI(RenderLayer.Opaque, idx++, cm, "shapeGeo", "box",
                    Matrix.Scaling(3.8f, 0.9f, 7.5f) * Matrix.Translation(px, 0.85f, pz));
                AddRI(RenderLayer.Opaque, idx++, "carRoof", "shapeGeo", "box",
                    Matrix.Scaling(3.0f, 0.8f, 4.2f) * Matrix.Translation(px, 1.75f, pz - 0.3f));
                float[] rwX = { -2.1f, 2.1f, -2.1f, 2.1f };
                float[] rwZ = { 2.5f, 2.5f, -2.5f, -2.5f };
                for (int w = 0; w < 4; w++)
                    AddRI(RenderLayer.Opaque, idx++, "carWheel", "shapeGeo", "sphere",
                        Matrix.Scaling(0.9f, 0.9f, 0.6f) * Matrix.Translation(px + rwX[w], 0.5f, pz + rwZ[w]));
                RegisterCollider(new Vector3(px, 0, pz), 2.5f);
            }
            for (int c = 0; c < 7; c++)
            {
                float lx = -28f + c * 8.0f;
                AddRI(RenderLayer.Opaque, idx++, "crosswalk", "shapeGeo", "box",
                    Matrix.Scaling(0.25f, 0.3f, 17f) * Matrix.Translation(lx, 0.13f, -80f));
            }

            // ─── HELPER: casa cu acoperis negru ──────────────────────────────────
            void AddHouseWithRoof(float hx, float hz, float sx, float sy, float sz, float ry, string hmat)
            {
                AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box",
                    Matrix.Scaling(sx + 4f, 0.15f, sz + 4f) * Matrix.Translation(hx, 0.08f, hz));
                AddRI(RenderLayer.Opaque, idx++, hmat, "shapeGeo", "box",
                    Matrix.Scaling(sx, sy, sz) * Matrix.RotationY(ry) * Matrix.Translation(hx, sy * 0.5f, hz));
                // Acoperis negru – placa principala
                AddRI(RenderLayer.Opaque, idx++, "roof", "shapeGeo", "box",
                    Matrix.Scaling(sx + 1.0f, 0.5f, sz + 1.0f) * Matrix.RotationY(ry) * Matrix.Translation(hx, sy + 0.25f, hz));
                // Coama acoperisului (negru)
                AddRI(RenderLayer.Opaque, idx++, "roof", "shapeGeo", "box",
                    Matrix.Scaling(sx * 0.25f, 0.8f, sz + 1.2f) * Matrix.RotationY(ry) * Matrix.Translation(hx, sy + 0.65f, hz));
                // Fronton stanga (negru)
                AddRI(RenderLayer.Opaque, idx++, "roof", "shapeGeo", "box",
                    Matrix.Scaling(sx + 1.2f, 0.4f, 0.4f) * Matrix.RotationX(0.45f) * Matrix.RotationY(ry) * Matrix.Translation(hx, sy + 0.2f, hz - sz * 0.5f - 0.2f));
                // Fronton dreapta (negru)
                AddRI(RenderLayer.Opaque, idx++, "roof", "shapeGeo", "box",
                    Matrix.Scaling(sx + 1.2f, 0.4f, 0.4f) * Matrix.RotationX(-0.45f) * Matrix.RotationY(ry) * Matrix.Translation(hx, sy + 0.2f, hz + sz * 0.5f + 0.2f));

                RegisterCollider(new Vector3(hx, 0, hz), (float)Math.Max(sx, sz) * 0.5f + 1.5f);
            }

            void AddPavementToRoad(float hx, float hz, float sx, float sz, float roadX, float roadZ, bool towardX)
            {
                if (towardX)
                {
                    float midX = (hx + roadX) * 0.5f;
                    float length = Math.Abs(roadX - hx) - sx * 0.5f - 1.0f;
                    if (length > 0)
                        AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box",
                            Matrix.Scaling(length, 0.12f, 2.5f) * Matrix.Translation(midX, 0.06f, hz));
                }
                else
                {
                    float midZ = (hz + roadZ) * 0.5f;
                    float length = Math.Abs(roadZ - hz) - sz * 0.5f - 1.0f;
                    if (length > 0)
                        AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box",
                            Matrix.Scaling(2.5f, 0.12f, length) * Matrix.Translation(hx, 0.06f, midZ));
                }
            }

            // ─── CLADIRI INTERIOARE CIRCUIT ───────────────────────────────────────
            var interiorHouses = new (float x, float z, float sx, float sy, float sz, float ry, string mat)[]
            {
                (-17f,-35f, 9f,11f, 9f, 0.0f,               "house"),
                (  0f,-40f,11f,14f, 9f, 0.0f,               "house2"),
                ( 17f,-35f, 9f,11f, 9f, MathUtil.PiOverFour, "house3"),
                (-19f,  0f,10f,13f,10f, 0.25f,              "house"),
                (  0f,  0f,13f,17f,11f, 0.0f,               "house2"),
                ( 19f,  0f,10f,13f,10f,-0.25f,              "house3"),
                (-17f, 35f, 9f,11f, 9f, 0.0f,               "house"),
                (  0f, 40f,11f,14f, 9f, 0.0f,               "house2"),
                ( 17f, 35f, 9f,11f, 9f, MathUtil.PiOverFour, "house3"),
            };
            foreach (var (hx, hz, sx, sy, sz, ry, hmat) in interiorHouses)
            {
                AddHouseWithRoof(hx, hz, sx, sy, sz, ry, hmat);
                if (hx < 0) AddPavementToRoad(hx, hz, sx, sz, -29f, hz, true);
                else if (hx > 0) AddPavementToRoad(hx, hz, sx, sz, 29f, hz, true);
                else
                {
                    if (hz < 0) AddPavementToRoad(hx, hz, sx, sz, hx, -55f, false);
                    else AddPavementToRoad(hx, hz, sx, sz, hx, 55f, false);
                }
            }

            // ─── CLADIRI EXTERIOARE STANGA (X in [-50..-80]) ─────────────────────
            var extLeft = new (float x, float z, float sx, float sy, float sz, float ry, string mat)[]
            {
                (-56f,-70f,11f,13f,10f, 0.0f, "house"),
                (-70f,-50f,13f,17f,12f, 0.4f, "house2"),
                (-60f,-25f,10f,14f, 9f, 0.0f, "house3"),
                (-75f,  0f,12f,16f,11f, 0.2f, "house"),
                (-58f, 25f,10f,12f,10f, 0.0f, "house2"),
                (-72f, 50f,14f,15f,12f,-0.3f, "house3"),
                (-57f, 70f,11f,13f,10f, 0.0f, "house"),
                (-68f,-90f,10f,11f, 9f, 0.1f, "house"),
                (-55f, 90f,12f,14f,11f,-0.1f, "house2"),
            };
            foreach (var (hx, hz, sx, sy, sz, ry, hmat) in extLeft)
            {
                AddHouseWithRoof(hx, hz, sx, sy, sz, ry, hmat);
                AddPavementToRoad(hx, hz, sx, sz, -41f, hz, true);
            }

            // ─── CLADIRI EXTERIOARE DREAPTA (dupa rau, X in [+85..+100]) ─────────
            // MUTATE mai departe de rau (raul ocupa X ~58-82 cu sinus)
            // Fata caselor la X=+85 minim, asa nu ating raul
            var extRight = new (float x, float z, float sx, float sy, float sz, float ry, string mat)[]
            {
                ( 90f,-70f,11f,13f,10f, 0.0f, "house"),
                ( 97f,-45f,13f,17f,12f, 0.4f, "house2"),
                ( 91f,-20f,10f,14f, 9f, 0.0f, "house3"),
                ( 98f,  0f,12f,16f,11f, 0.2f, "house"),   // <-- eliminat cel de la Z=0 care era lipit de pod
                ( 90f, 25f,10f,12f,10f, 0.0f, "house2"),
                ( 97f, 50f,14f,15f,12f,-0.3f, "house3"),
                ( 90f, 70f,11f,13f,10f, 0.0f, "house"),
                ( 98f,-90f,10f,11f, 9f, 0.1f, "house"),
                ( 90f, 90f,12f,14f,11f,-0.1f, "house2"),
            };
            foreach (var (hx, hz, sx, sy, sz, ry, hmat) in extRight)
            {
                // Sarim casa de la Z~0 daca e prea aproape de pod (Z in [-10, +10])
                if (Math.Abs(hz) < 12f && hx > 85f)
                {
                    // Casa la Z=0, X=98: e departe de pod (pod la Z=0, X:40-83), deci ok
                    // Dar verificam sa nu fie in zona podului
                    if (hx - sx * 0.5f < 85f) continue; // prea aproape de rau
                }
                AddHouseWithRoof(hx, hz, sx, sy, sz, ry, hmat);
                AddPavementToRoad(hx, hz, sx, sz, 83f, hz, true);
            }

            // ─── RAU – centru X=+70, sinusoidal, Z=-150..+150 ───────────────────
            AddRI(RenderLayer.Transparent, idx++, "water", "shapeGeo", "river",
                Matrix.Translation(70.0f, 0.3f, 0.0f));

            // ─── GARDURI PE MALUL RAULUI ──────────────────────────────────────────
            for (float gz = -145f; gz <= 145f; gz += 5.0f)
            {
                if (Math.Abs(gz) < 8.0f) continue; // evita zona podului

                float sinOff = RiverSinOffset(gz);

                float fenceLeftX = 57.5f + sinOff;
                AddRI(RenderLayer.Opaque, idx++, "fence", "shapeGeo", "box",
                    Matrix.Scaling(0.2f, 1.4f, 0.2f) * Matrix.Translation(fenceLeftX, 0.7f, gz));
                if (Math.Abs(gz % 5.0f) < 0.1f)
                    AddRI(RenderLayer.Opaque, idx++, "fence", "shapeGeo", "box",
                        Matrix.Scaling(0.15f, 0.25f, 5.1f) * Matrix.Translation(fenceLeftX, 0.9f, gz + 2.5f));

                float fenceRightX = 82.5f + sinOff;
                AddRI(RenderLayer.Opaque, idx++, "fence", "shapeGeo", "box",
                    Matrix.Scaling(0.2f, 1.4f, 0.2f) * Matrix.Translation(fenceRightX, 0.7f, gz));
                if (Math.Abs(gz % 5.0f) < 0.1f)
                    AddRI(RenderLayer.Opaque, idx++, "fence", "shapeGeo", "box",
                        Matrix.Scaling(0.15f, 0.25f, 5.1f) * Matrix.Translation(fenceRightX, 0.9f, gz + 2.5f));
            }

            // Colizor rau – excluzand zona podului (podul e la Z in [-7,+7])
            // Implementat ca 2 colizori: rau Nord si rau Sud
            RegisterCollider(new Vector3(70f, 0, 80f), 12f); // rau Nord (Z > 8)
            RegisterCollider(new Vector3(70f, 0, -80f), 12f); // rau Sud  (Z < -8)

            // ─── POD PESTE RAU ────────────────────────────────────────────────────
            float bridgeCX = 61.5f;
            float bridgeLen = 43.0f;
            float bridgeY = 0.4f;
            AddRI(RenderLayer.Opaque, idx++, "pavement", "shapeGeo", "box",
                Matrix.Scaling(bridgeLen, 0.5f, 12f) * Matrix.Translation(bridgeCX, bridgeY, 0f));
            // Balustrade
            AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                Matrix.Scaling(bridgeLen, 1.5f, 0.4f) * Matrix.Translation(bridgeCX, bridgeY + 1.0f, -6.2f));
            AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                Matrix.Scaling(bridgeLen, 1.5f, 0.4f) * Matrix.Translation(bridgeCX, bridgeY + 1.0f, 6.2f));
            // Piloni pod
            for (float px2 = 45f; px2 <= 80f; px2 += 18f)
                AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                    Matrix.Scaling(1.3f, 4.5f, 1.3f) * Matrix.Translation(px2, -1.5f, 0f));
            // Lampi pod
            for (float blz = -5f; blz <= 5f; blz += 10f)
                for (float blx = bridgeCX - 13f; blx <= bridgeCX + 13f; blx += 13f)
                {
                    AddRI(RenderLayer.Opaque, idx++, "lampPost", "shapeGeo", "box",
                        Matrix.Scaling(0.3f, 5f, 0.3f) * Matrix.Translation(blx, bridgeY + 2.5f, blz));
                    AddRI(RenderLayer.Opaque, idx++, "lightBulb", "shapeGeo", "sphere",
                        Matrix.Scaling(1f, 1f, 1f) * Matrix.Translation(blx, bridgeY + 5.3f, blz));
                }

            // ─── COPACI ───────────────────────────────────────────────────────────
            var trees = new List<(float x, float z, string mat, float sc)>();

            float[] txsL = { -48f, -55f, -63f, -72f, -52f, -65f, -78f, -50f, -60f, -70f, -48f, -56f, -68f, -76f, -53f, -62f, -73f, -49f, -59f, -71f };
            float[] tzsL = { -95f, -85f, -75f, -65f, -55f, -42f, -30f, -18f, -5f, 7f, 19f, 30f, 42f, 55f, 65f, 75f, 85f, 95f, 105f, -105f };
            string[] tmL = { "tree", "tree2", "tree", "tree", "tree2", "tree", "tree2", "tree", "tree", "tree2", "tree", "tree2", "tree", "tree", "tree2", "tree", "tree2", "tree", "tree", "tree2" };
            float[] tsL = { 11f, 13f, 12f, 10f, 14f, 11f, 13f, 10f, 12f, 14f, 11f, 13f, 10f, 14f, 12f, 11f, 13f, 10f, 12f, 14f };
            for (int t = 0; t < txsL.Length; t++) trees.Add((txsL[t], tzsL[t], tmL[t], tsL[t]));

            float[] txsR = { 95f, 102f, 106f, 99f, 104f, 109f, 97f, 103f, 107f, 100f, 105f, 110f, 98f, 102f, 107f, 96f, 104f, 109f, 99f, 103f };
            float[] tzsR = { -95f, -85f, -75f, -65f, -55f, -42f, -30f, -18f, -5f, 7f, 19f, 30f, 42f, 55f, 65f, 75f, 85f, 95f, 105f, -105f };
            string[] tmR = { "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2", "tree", "tree2" };
            float[] tsR = { 12f, 14f, 10f, 13f, 11f, 14f, 10f, 12f, 14f, 11f, 13f, 10f, 12f, 14f, 11f, 13f, 10f, 12f, 14f, 11f };
            for (int t = 0; t < txsR.Length; t++) trees.Add((txsR[t], tzsR[t], tmR[t], tsR[t]));

            float[] alignZ = { -70f, -75f, -80f, -85f, 70f, 75f, 80f, 85f };
            float[] alignX = { -30f, -20f, -10f, 0f, 10f, 20f, 30f };
            foreach (float az in alignZ)
                foreach (float ax in alignX)
                {
                    if (az < -69f && az > -91f && ax > -29f && ax < 11f) continue;
                    trees.Add((ax, az, Math.Abs(ax + az) % 2 == 0 ? "tree" : "tree2",
                               9f + ((Math.Abs((int)(ax * az))) % 4)));
                }

            trees.Add((-8f, -20f, "tree", 8f)); trees.Add((8f, -20f, "tree2", 8f));
            trees.Add((-8f, 20f, "tree", 8f)); trees.Add((8f, 20f, "tree2", 8f));

            foreach (var (tx, tz, tmat, tsc) in trees)
                AddRI(RenderLayer.Transparent, idx++, tmat, "shapeGeo", "tree",
                    Matrix.Scaling(tsc, tsc, tsc) * Matrix.Translation(tx, 0, tz));

            // ─── CER ──────────────────────────────────────────────────────────────
            AddRI(RenderLayer.Sky, idx++, "sky", "shapeGeo", "sphere", Matrix.Scaling(5000f));

            // ─── TEREN ────────────────────────────────────────────────────────────
            AddRI(RenderLayer.Opaque, idx++, "grassMaterial", "shapeGeo", "grid",
                Matrix.Translation(0, 0, 0), Matrix.Scaling(60f, 60f, 1f));

            // ─── STICKMEN ─────────────────────────────────────────────────────────
            AddStickman(idx, new Vector3(-28f, 0, -55f), 0f, 0f, true, ref idx);

            var smData = new (float x, float z, float angle, float speed, string cloth)[]
            {
                (-38f,-45f, 0.5f, 2.8f, "stickClothes"),
                (-38f, 20f, 1.2f, 3.2f, "stickRed"),
                (-38f, 50f, 2.0f, 2.5f, "stickClothes"),
                (-38f,-15f, 3.5f, 3.0f, "stickGreen"),
                (-38f,  5f, 5.0f, 2.7f, "stickOrange"),
                (-38f,-60f, 0.8f, 3.5f, "stickClothes"),
                ( 38f,-45f, 1.0f, 2.9f, "stickPurple"),
                ( 38f, 20f, 2.5f, 3.1f, "stickClothes"),
                ( 38f, 50f, 4.0f, 2.6f, "stickRed"),
                ( 38f,-15f, 0.3f, 2.8f, "stickGreen"),
                ( 38f,  5f, 1.8f, 3.4f, "stickClothes"),
                ( 38f, 60f, 3.2f, 2.5f, "stickOrange"),
                (-15f,-25f, 1.5f, 2.0f, "stickClothes"),
                ( 15f, 10f, 3.0f, 1.8f, "stickPurple"),
                (  0f, 30f, 0.7f, 2.2f, "stickGreen"),
                (-10f, 45f, 4.5f, 1.9f, "stickClothes"),
            };
            foreach (var (sx, sz, sa, ss, sc) in smData)
                AddStickman(idx, new Vector3(sx, 0, sz), sa, ss, false, ref idx, sc);
        }

        // =========================================================================
        private void AddStickman(int startIdx, Vector3 pos, float angle, float speed,
            bool isPlayer, ref int idx, string clothMat = null)
        {
            MeshGeometry geo = geometries["shapeGeo"];
            string skinMat = isPlayer ? "stickPlayer" : "stickSkin";
            string bodyMat = isPlayer ? "stickPlayer" : (clothMat ?? "stickClothes");

            var sm = new Stickman(pos, angle, speed, isPlayer)
            {
                Head = MakeSphereRI(geo, materials[skinMat], idx++),
                Torso = MakeBoxRI(geo, materials[bodyMat], idx++),
                ArmL = MakeBoxRI(geo, materials[bodyMat], idx++),
                ArmR = MakeBoxRI(geo, materials[bodyMat], idx++),
                LegL = MakeBoxRI(geo, materials[skinMat], idx++),
                LegR = MakeBoxRI(geo, materials[skinMat], idx++),
            };

            foreach (var ri in new[] { sm.Head, sm.Torso, sm.ArmL, sm.ArmR, sm.LegL, sm.LegR })
            { renderItemLayers[RenderLayer.Opaque].Add(ri); allRenderItems.Add(ri); }
            stickmen.Add(sm);
        }

        // =========================================================================
        private RenderItem MakeBoxRI(MeshGeometry geo, Material mat, int cbIdx)
        {
            var s = geo.DrawArguments["box"];
            return new RenderItem
            {
                ObjCBIndex = cbIdx,
                Material = mat,
                MeshGeometry = geo,
                IndexCount = s.IndexCount,
                StartIndexLocation = s.StartIndexLocation,
                BaseVertexLocation = s.BaseVertexLocation,
                World = Matrix.Identity,
                TexTransform = Matrix.Identity,
                NumFramesDirty = NUMBER_OF_FRAME_RESOURCES
            };
        }

        private RenderItem MakeSphereRI(MeshGeometry geo, Material mat, int cbIdx)
        {
            var s = geo.DrawArguments["sphere"];
            return new RenderItem
            {
                ObjCBIndex = cbIdx,
                Material = mat,
                MeshGeometry = geo,
                IndexCount = s.IndexCount,
                StartIndexLocation = s.StartIndexLocation,
                BaseVertexLocation = s.BaseVertexLocation,
                World = Matrix.Identity,
                TexTransform = Matrix.Identity,
                NumFramesDirty = NUMBER_OF_FRAME_RESOURCES
            };
        }

        private void AddRI(RenderLayer layer, int cbIdx,
            string mat, string geoName, string sub,
            Matrix world, Matrix? texTr = null)
        {
            var g = geometries[geoName];
            var s = g.DrawArguments[sub];
            var ri = new RenderItem
            {
                ObjCBIndex = cbIdx,
                Material = materials[mat],
                MeshGeometry = g,
                IndexCount = s.IndexCount,
                StartIndexLocation = s.StartIndexLocation,
                BaseVertexLocation = s.BaseVertexLocation,
                World = world,
                TexTransform = texTr ?? Matrix.Identity
            };
            renderItemLayers[layer].Add(ri);
            allRenderItems.Add(ri);
        }

        private void DrawRenderItems(GraphicsCommandList cmdList, IList<RenderItem> items)
        {
            int sz = D3DHelper.ComputeConstantBufferByteSize<ObjectConstants>();
            Resource cb = CurrentFrameResource.ObjectCB.Resource;
            foreach (var ri in items)
            {
                cmdList.SetVertexBuffer(0, ri.MeshGeometry.VertexBufferView);
                cmdList.SetIndexBuffer(ri.MeshGeometry.IndexBufferView);
                cmdList.PrimitiveTopology = ri.PrimitiveType;
                cmdList.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress + ri.ObjCBIndex * sz);
                cmdList.DrawIndexedInstanced(ri.IndexCount, 1, ri.StartIndexLocation, ri.BaseVertexLocation, 0);
            }
        }

        private static StaticSamplerDescription[] GetStaticSamplers() => new[]
        {
            new StaticSamplerDescription(ShaderVisibility.All,0,0){ Filter=Filter.MinMagMipPoint,   AddressUVW=TextureAddressMode.Wrap },
            new StaticSamplerDescription(ShaderVisibility.All,1,0){ Filter=Filter.MinMagMipPoint,   AddressUVW=TextureAddressMode.Clamp },
            new StaticSamplerDescription(ShaderVisibility.All,2,0){ Filter=Filter.MinMagMipLinear,  AddressUVW=TextureAddressMode.Wrap },
            new StaticSamplerDescription(ShaderVisibility.All,3,0){ Filter=Filter.MinMagMipLinear,  AddressUVW=TextureAddressMode.Clamp },
            new StaticSamplerDescription(ShaderVisibility.All,4,0){ Filter=Filter.Anisotropic,      AddressUVW=TextureAddressMode.Wrap,  MipLODBias=0f, MaxAnisotropy=8 },
            new StaticSamplerDescription(ShaderVisibility.All,5,0){ Filter=Filter.Anisotropic,      AddressUVW=TextureAddressMode.Clamp, MipLODBias=0f, MaxAnisotropy=8 }
        };
    }
}