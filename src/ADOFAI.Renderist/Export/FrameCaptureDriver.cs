using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using ADOFAI.Renderist.Logging;

namespace ADOFAI.Renderist.Export
{
    /// <summary>
    /// 确定性编辑器导出的同步帧末事务后端（Phase 3.7.0 Custom Resolution &amp; Supersampling）。
    /// 支持两种冻结模式：PNG 序列（image output enabled）与 log-only（image output disabled）。
    ///
    /// Render Source：ADOFAI 原生谱面摄像机链（scrCamera.Bgcamstatic / BGcam / camobj）
    /// 的 targetTexture 在本 session 内被接管到 Renderist-owned RenderTexture。
    /// Unity 仍按正常帧渲染流程渲染这三台 Camera，Screen Space UI 不进入该 RT。
    ///
    /// Output geometry：捕获尺寸与统一 Camera aspect 由 scheduler 在 session 开始时冻结后
    /// 传入 <see cref="TryActivateCameraSource"/>，本类**不再**读取 Screen.width / Screen.height。
    /// 因此窗口尺寸在 session 中途变化不会造成 RT 尺寸与 aspect ownership 不一致。
    /// PNG 与 log-only 共用同一尺寸、同一个 RenderTexture 与同一个 aspect。
    ///
    /// Camera aspect ownership：三台 Camera 统一使用冻结输出的 aspect，并各自登记 ownership。
    /// 释放时只在当前值仍等于 Renderist 写入值时才 <c>ResetAspect()</c>（恢复 Unity 自动行为）；
    /// 已被外部流程改写的值不覆盖。Renderist 不通过“旧 aspect 是否等于屏幕 aspect”来推断
    /// 自动模式——该推断在 targetTexture 接管后不可靠。
    ///
    /// 捕获点：WaitForEndOfFrame。帧末事务在同一处完成，并按 session 开始时冻结的输出模式分支：
    ///   * PNG（imageOutputEnabled = true）：ReadPixels → EncodeToPNG → File.WriteAllBytes 成功
    ///     才算一帧已捕获（imageWritten = true）。
    ///   * Log-only（imageOutputEnabled = false）：source / generation / pending-index 校验成功后
    ///     直接返回成功的帧末事务结果（imageWritten = false，filePath = null），
    ///     不执行 EnsureTexture / Texture2D 创建 / ReadPixels / Texture2D.Apply / EncodeToPNG /
    ///     File.WriteAllBytes，也不构造 PNG 文件路径。
    /// 两种模式共用同一个结果回调入口与同一套 EOF coroutine / generation 隔离 / cleanup；
    /// 不调用 Camera.Render / ScreenCapture；不创建替代 Camera；不依赖异步完成回调。
    ///
    /// generation 机制：每次成功 Start 分配一个唯一 generation，旧 session 的 EndOfFrame callback
    /// 不会污染新 session。
    ///
    /// 两阶段生命周期：
    ///   Start()                    → generation / host / CaptureHostBehaviour / coroutine
    ///   TryActivateCameraSource()  → 取得当前 session 的 Camera 链并接管 targetTexture
    /// Start 不得假定 scrCamera 摄像机链已经可用；source 未激活时 RequestCapture 一律拒绝，
    /// 绝不回退到 Screen framebuffer。
    ///
    /// Activation ownership 不变量：
    ///   * RenderTexture 一旦创建成功，任何失败路径都必须二选一：Release + Destroy 均成功，
    ///     或把该引用保存在本类可观察、可重试的 ownership 中（`_captureTarget`），
    ///     绝不作为 local reference 丢失。
    ///   * Camera refs / saved old targets / **saved baseline aspect** / 本次写入的统一 aspect
    ///     同样在**第一次 Camera 写入之前**就登记，因此 partial camera assignment 天然属于
    ///     `_sourceActive` 的 ownership transaction，由同一个 `RestoreCameraSource` 收敛
    ///     （不新增第二套 partial cleanup）。
    ///   * targetTexture 与 aspect 使用**各自独立**的释放路径（RelinquishTargetTexture /
    ///     RelinquishAspect），但收敛判定合并到同一个 `_sourceActive`：因此 setter 成功之后
    ///     读回失败、或 aspect 恢复失败，都不会漏掉 partial assignment，也不会提前清空 ownership。
    ///   * 只有 `IsCaptureTargetStillReferenced() == false` 时才 Release / Destroy RT。
    /// </summary>
    internal static class FrameCaptureDriver
    {
        /// <summary>
        /// 帧末事务结果回调（在 Unity 主线程 WaitForEndOfFrame 之后调用），PNG 与 log-only
        /// 两种模式共用同一个入口。
        /// frameIndex 是 canonical output frame number，类型为 long（合法 Output FPS
        /// 为任意正 int，帧号不能依赖 int）。
        /// imageWritten = true 表示本帧确实成功写盘 PNG，且 filePath 是实际写入路径；
        /// imageWritten = false（log-only）表示本帧只完成了帧末事务，filePath 必为 null。
        /// </summary>
        public delegate void CaptureResultCallback(
            long generation, long frameIndex, bool success, bool imageWritten, string filePath, string error);

        /// <summary>本阶段 Render Source 标签；写入 session metadata。</summary>
        public const string CameraSourceLabel = "scrCamera-rendertexture";

        private const string CaptureTargetName = "ADOFAI.Renderist.CaptureTarget";

        /// <summary>
        /// 判定「baseline 三台 aspect 是否互相兼容」以及「当前 aspect 是否仍是 Renderist
        /// 写入值」的相对容差。只吸收浮点表示 / native 往返误差，不构成任何 aspect 合法区间，
        /// 也不是对 aspect 取值的限制。
        /// </summary>
        private const float AspectTolerance = 1e-4f;

        private static GameObject _host;
        private static CaptureHostBehaviour _behaviour;

        private static long _generationCounter;
        private static long _activeGeneration;

        // ---- Render Source ownership（只在本 session 内有效）----
        //
        // 语义：Camera source ownership **transaction** 是否已开始（可能 partial）。
        // 接管前会先把 captureTarget / capture dimensions / camera refs / saved old targets
        // 全部登记，再逐个写入 Camera，因此 `_sourceActive == true` 不代表"三台已全部接管"，
        // 只代表"partial assignment 也已进入统一 restore 路径"。
        // 写入失败即由 RestoreCameraSource 收敛；未能收敛时状态保留供 Stop 重试。
        private static bool _sourceActive;
        private static RenderTexture _captureTarget;
        private static int _captureWidth;
        private static int _captureHeight;
        private static Camera _bgStaticCamera;
        private static Camera _bgCamera;
        private static Camera _mainCamera;
        private static RenderTexture _oldBgStaticTarget;
        private static RenderTexture _oldBgTarget;
        private static RenderTexture _oldMainTarget;

        // ---- Camera aspect ownership（与 targetTexture 独立登记、合并收敛）----
        //
        // _writtenAspect：本次 session 写入三台 Camera 的统一输出 aspect；0 = 未写入。
        // _oldXxxAspect：**第一次 aspect 写入之前**读到的真实 baseline，同时用于
        //   (a) 激活前的兼容性 fail-closed 判定记录，(b) residual 诊断。
        // 这些字段与 _captureTarget / Camera refs 一样在第一次 Camera 写入之前登记，
        // 因此 setter 成功后读回失败也不会漏掉 partial assignment。
        private static float _writtenAspect;
        private static float _oldBgStaticAspect;
        private static float _oldBgAspect;
        private static float _oldMainAspect;

        // 已完成 aspect 写入的 Camera 台数（按 Bgcamstatic → BGcam → camobj 顺序）。
        //
        // 这是**精确**的“本次到底写过哪几台”的记录：每个 setter 成功后立即递增，
        // 不依赖任何读回。因此：
        //   * setter 抛异常 → 该台不计入，cleanup 不会去 ResetAspect 一台没写过的 Camera；
        //   * 读回失败不影响该值（读回只用于日志）。
        // 未写入的 Camera 即使在数值上恰好等于目标 aspect（例如 legacy 模式下窗口 aspect
        // 就等于输出 aspect），也绝不会被 ResetAspect 覆盖。
        private static int _aspectAssignedCount;

        public static bool IsRunning => _host != null && _behaviour != null;

        /// <summary>
        /// Camera source ownership transaction 是否已开始（**可能 partial**）。
        /// cleanup ownership 追踪用：为 true 时 RestoreCameraSource 会逐 Camera 做
        /// ownership-aware 恢复；未成功收敛前不得视作"已释放"。
        /// </summary>
        public static bool HasActiveCameraSource => _sourceActive;

        /// <summary>
        /// 是否仍持有 Renderist-owned capture target。source 已经从 Camera 上 relinquish 后，
        /// Release / Destroy 若失败，target 仍属于 residual ownership，必须保留到下一次 Stop 重试。
        /// </summary>
        public static bool HasOwnedCaptureTarget => _captureTarget != null;

        /// <summary>
        /// 把本 session capture target 的只读形态（format / graphicsFormat / MSAA / mipmap /
        /// 冻结尺寸）写入运行时 inventory，供 session metadata 记录。
        /// 只读：不修改 RenderTexture 的任何属性；target 未激活时为 no-op。
        /// </summary>
        public static void CaptureRenderTargetInventory(RenderEnvironmentInventory inventory)
        {
            if (inventory == null) return;

            RenderTexture target = _captureTarget;
            if (target == null) return;

            inventory.CaptureRenderTarget(target, _captureWidth, _captureHeight);
        }

        /// <summary>本 session 冻结的捕获尺寸；未激活时为 0。</summary>
        public static int CaptureWidth => _captureWidth;

        public static int CaptureHeight => _captureHeight;

        /// <summary>
        /// 本 session 写入三台 Camera 的统一输出 aspect（width / height）；
        /// 未接管 aspect 时为 0。仅供 metadata / 诊断使用。
        /// </summary>
        public static float CaptureAspect => _writtenAspect;

        public static bool Start(
            string outputDirectory,
            string prefix,
            int zeroPadWidth,
            CaptureResultCallback onResult,
            bool imageOutputEnabled,
            out long generation,
            out string error)
        {
            generation = 0;
            error = null;

            if (IsRunning)
            {
                error = "already-running";
                return false;
            }

            if (string.IsNullOrEmpty(outputDirectory))
            {
                error = "output-directory-empty";
                return false;
            }

            GameObject host = null;
            CaptureHostBehaviour behaviour = null;
            try
            {
                generation = ++_generationCounter;
                _activeGeneration = generation;
                // 新 generation 从"未写过任何 aspect"开始；上一次 session 的 ownership
                // 必须先由 Stop() 收敛（Start 不接管未释放的 ownership）。
                _aspectAssignedCount = 0;

                host = new GameObject("ADOFAI.Renderist.FrameCaptureDriver");
                host.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(host);

                behaviour = host.AddComponent<CaptureHostBehaviour>();
                behaviour.Configure(outputDirectory, string.IsNullOrEmpty(prefix) ? "frame_" : prefix,
                    zeroPadWidth < 1 ? 1 : zeroPadWidth, onResult, generation, imageOutputEnabled);

                // 静态 ownership 只在全部启动步骤成功后交接；此前 host/behaviour
                // 属于局部 ownership，中途异常由 catch 就地清理。
                _host = host;
                _behaviour = behaviour;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 启动失败", ex);
                // 局部清理：静态 _host/_behaviour 从未接管，不能调用 Stop()。
                // behaviour.Shutdown() 的次生异常不得阻止 host Destroy。
                if (behaviour != null)
                {
                    try { behaviour.Shutdown(); }
                    catch (Exception shutdownEx)
                    {
                        Log.Exception("FrameCaptureDriver: 启动失败后 Shutdown 局部 behaviour 失败", shutdownEx);
                    }
                }
                if (host != null)
                {
                    try { UnityEngine.Object.Destroy(host); }
                    catch (Exception destroyEx)
                    {
                        Log.Exception("FrameCaptureDriver: 启动失败后 Destroy 局部 host 失败", destroyEx);
                    }
                }
                _activeGeneration = 0;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 激活 Render Source：把当前 session 的 ADOFAI 原生摄像机链
        /// （Bgcamstatic / BGcam / camobj）的 targetTexture 指向 Renderist-owned RenderTexture，
        /// 并把三台 Camera 的 aspect 统一到 session 开始时冻结的输出 aspect。
        ///
        /// Start 不负责这件事；真正接管必须等到 scheduler 的 InitializationHold readiness
        /// 满足、即将进入 Capturing 之前。此时 scrCamera 与三台 Camera 才必然可用。
        ///
        /// width / height 是 scheduler 在 session 开始时冻结的输出几何（自定义分辨率或
        /// 冻结的窗口尺寸）；本方法**不读 Screen**，因此 session 中途改变窗口不影响本 session。
        ///
        /// 激活前的 aspect baseline 检查（在任何 Camera 写入之前完成，fail-closed）：
        ///   * 三台 aspect 必须可读、有限且为正；
        ///   * 三台 baseline 必须互相兼容（同一 session 内它们共用同一个渲染目标 aspect）。
        /// 任一不满足即拒绝激活，并保持「尚未接触任何 Camera」，绝不写一半再失败。
        /// 注意：**不**用「baseline 是否与屏幕 aspect 数值相等」来推断自动模式——
        /// targetTexture 已由游戏接管时该推断不成立，因此不作为判据。
        ///
        /// 成功后才允许 RequestCapture。失败返回 false + machine-readable error；
        /// 调用方必须让 session 失败，不得回退到 Screen framebuffer。
        /// </summary>
        public static bool TryActivateCameraSource(long generation, int width, int height, out string error)
        {
            error = null;

            if (generation != _activeGeneration)
            {
                error = "generation-mismatch";
                return false;
            }
            if (!IsRunning)
            {
                error = "driver-not-running";
                return false;
            }
            if (_sourceActive)
            {
                error = "capture-source-already-active";
                return false;
            }

            // 冻结几何由调用方提供：驱动不存在第二个分辨率来源。
            if (width <= 0 || height <= 0)
            {
                error = "capture-dimensions-invalid";
                return false;
            }

            // 每次新的 capture ownership 都重新取得当前 session 的对象。
            if (!EditorGameReflection.TryReadChartCameraChain(
                    out Camera bgStaticCamera, out Camera bgCamera, out Camera mainCamera,
                    out string chainError))
            {
                error = chainError ?? "camera-chain-unavailable";
                return false;
            }

            RenderTexture target;
            try
            {
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.name = CaptureTargetName;
                target.antiAliasing = 1;
                target.useMipMap = false;
                target.autoGenerateMips = false;
                target.Create();
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 创建 capture target 失败", ex);
                error = "capture-target-create-failed:" + ex.Message;
                return false;
            }

            // 从这里开始 target 已存在：任何失败路径都必须"销毁成功"或"保留为 ownership"。
            if (!target.IsCreated())
            {
                DiscardOrRetainUntouchedTarget(target);
                error = "capture-target-not-created";
                return false;
            }

            // 保存真实旧值：不得假定原值为 null。getter 也可能抛异常（例如 Camera 已销毁），
            // 因此整体受保护，失败时同样走"销毁成功或保留 ownership"。
            RenderTexture oldBgStaticTarget;
            RenderTexture oldBgTarget;
            RenderTexture oldMainTarget;
            try
            {
                oldBgStaticTarget = bgStaticCamera.targetTexture;
                oldBgTarget = bgCamera.targetTexture;
                oldMainTarget = mainCamera.targetTexture;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 读取 Camera 旧 targetTexture 失败", ex);
                DiscardOrRetainUntouchedTarget(target);
                error = "capture-source-saved-target-unavailable:" + ex.Message;
                return false;
            }

            // ---- 三台 Camera 的 aspect baseline（仍在任何 Camera 写入之前）----
            // 读取失败 / 非法 / 三台互不兼容都在这里 fail-closed，此时尚未写过任何 Camera。
            if (!TryReadCameraAspect(bgStaticCamera, "Bgcamstatic", out float oldBgStaticAspect, out string aspectReadError) ||
                !TryReadCameraAspect(bgCamera, "BGcam", out float oldBgAspect, out aspectReadError) ||
                !TryReadCameraAspect(mainCamera, "camobj", out float oldMainAspect, out aspectReadError))
            {
                DiscardOrRetainUntouchedTarget(target);
                error = aspectReadError;
                return false;
            }

            if (!IsAspectBaselineCompatible(
                    oldBgStaticAspect, oldBgAspect, oldMainAspect, out string aspectBaselineDetail))
            {
                DiscardOrRetainUntouchedTarget(target);
                error = "capture-aspect-baseline-incompatible:" + aspectBaselineDetail;
                return false;
            }

            // 统一输出 aspect：由冻结几何派生，三台 Camera 使用同一个值。
            float unifiedAspect = (float)((double)width / (double)height);

            // 在**第一次 Camera 写入之前**登记 ownership：partial assignment 也必须可见、可重试。
            // 从此 _sourceActive 表示"ownership transaction 已开始（可能 partial）"。
            // targetTexture 与 aspect 同时登记，因此 setter 成功后读回失败也不会漏掉 partial。
            _captureTarget = target;
            _captureWidth = width;
            _captureHeight = height;
            _writtenAspect = unifiedAspect;
            _bgStaticCamera = bgStaticCamera;
            _bgCamera = bgCamera;
            _mainCamera = mainCamera;
            _oldBgStaticTarget = oldBgStaticTarget;
            _oldBgTarget = oldBgTarget;
            _oldMainTarget = oldMainTarget;
            _oldBgStaticAspect = oldBgStaticAspect;
            _oldBgAspect = oldBgAspect;
            _oldMainAspect = oldMainAspect;
            _sourceActive = true;

            try
            {
                bgStaticCamera.targetTexture = target;
                bgCamera.targetTexture = target;
                mainCamera.targetTexture = target;

                // aspect ownership：三台统一到冻结输出 aspect。任何一个 setter 抛异常，
                // 已写入的部分都由同一个 RestoreCameraSource 收敛（不新增第二套 cleanup）。
                // 每台写入成功后立即记数，因此 cleanup 只会 ResetAspect 真正写过的 Camera。
                bgStaticCamera.aspect = unifiedAspect;
                _aspectAssignedCount = 1;
                bgCamera.aspect = unifiedAspect;
                _aspectAssignedCount = 2;
                mainCamera.aspect = unifiedAspect;
                _aspectAssignedCount = 3;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 接管 camera source 失败，交由统一 ownership cleanup 收敛", ex);
                error = "capture-source-assign-failed:" + ex.Message;
                ConvergeSourceOwnershipAfterFailedAssignment();
                return false;
            }

            // setter 之后的读回**只用于日志诊断**：ownership 已在写入前登记，
            // 读回失败绝不改变 ownership，也不会漏掉 partial assignment。
            VerifyAspectAppliedBestEffort(bgStaticCamera, unifiedAspect, "Bgcamstatic");
            VerifyAspectAppliedBestEffort(bgCamera, unifiedAspect, "BGcam");
            VerifyAspectAppliedBestEffort(mainCamera, unifiedAspect, "camobj");

            // 只在 source activate 时记录一次完整 inventory；不逐帧刷日志。
            Log.Info("FrameCaptureDriver: capture source active source=" + CameraSourceLabel +
                     " size=" + width.ToString(CultureInfo.InvariantCulture) + "x" +
                     height.ToString(CultureInfo.InvariantCulture) +
                     " unifiedAspect=" + unifiedAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                     " baselineAspect={Bgcamstatic=" +
                     oldBgStaticAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                     ",BGcam=" + oldBgAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                     ",camobj=" + oldMainAspect.ToString("0.######", CultureInfo.InvariantCulture) + "}" +
                     " target=" + CaptureTargetName +
                     " " + DescribeCamera("Bgcamstatic", bgStaticCamera, oldBgStaticTarget) +
                     " " + DescribeCamera("BGcam", bgCamera, oldBgTarget) +
                     " " + DescribeCamera("camobj", mainCamera, oldMainTarget));
            return true;
        }

        /// <summary>
        /// 请求在下一个 WaitForEndOfFrame 完成指定输出帧的**帧末事务**。同帧内会被去重。
        /// PNG 与 log-only 都走这一条入口；是否真的读回 / 编码 / 写盘由 session 开始时
        /// 冻结的 <c>_imageOutputEnabled</c> 决定。
        /// generation 与当前 active generation 不一致、或 Render Source 尚未激活时返回 false。
        /// 绝不回退到 Screen framebuffer。
        /// </summary>
        public static bool RequestCapture(long generation, long frameIndex)
        {
            if (generation != _activeGeneration) return false;
            if (!_sourceActive || _captureTarget == null) return false;
            CaptureHostBehaviour b = _behaviour;
            if (b == null) return false;
            b.RequestCapture(frameIndex);
            return true;
        }

        /// <summary>
        /// target 已创建但**尚未接触任何 Camera** 时的失败收敛：
        /// 先尝试完整 Release + Destroy；失败则把引用交给 ownership（`_captureTarget`，
        /// 不伪造 Camera ownership、`_sourceActive` 保持 false），由 Stop /
        /// RestoreCameraSource 的 source-inactive retry path 继续收敛。
        /// 绝不把已创建的 RenderTexture 作为 local reference 丢弃。
        /// </summary>
        private static void DiscardOrRetainUntouchedTarget(RenderTexture target)
        {
            if (TryDestroyTexture(target))
                return;

            _captureTarget = target;
            Log.Warn("FrameCaptureDriver: capture target 销毁失败，保留 ownership 供下一次 Stop 重试");
        }

        /// <summary>
        /// Camera 接管过程中失败后的统一收敛：直接把已登记的 ownership（captureTarget +
        /// Camera refs + saved old targets）交给既有 <see cref="RestoreCameraSource"/>。
        /// partial assignment 与完整 assignment 走同一条 ownership-aware 路径：
        ///   * cleanup 成功 → ownership 全清，返回 false 让 scheduler fail-closed；
        ///   * cleanup 失败 → 状态原样保留为 residual ownership，下一次 Stop 继续重试。
        /// 不新增第二套 partial-source cleanup 状态机，也不吞 cleanup 异常。
        /// </summary>
        private static void ConvergeSourceOwnershipAfterFailedAssignment()
        {
            bool restored;
            try
            {
                restored = RestoreCameraSource();
            }
            catch (Exception cleanupEx)
            {
                restored = false;
                Log.Exception("FrameCaptureDriver: partial capture source cleanup 异常，保留 ownership 供重试", cleanupEx);
            }

            if (!restored)
            {
                Log.Warn("FrameCaptureDriver: partial capture source ownership 未收敛，" +
                         "保留 captureTarget / Camera refs 供下一次 Stop 重试");
            }
        }

        /// <summary>
        /// 停止并释放 host / coroutine / 复用纹理，并精确恢复 Render Source ownership。幂等。
        /// generation 先失效；Shutdown / capture source restore / host Destroy 各自独立收敛。
        /// host / behaviour 引用只有在 Destroy(host) 返回成功后才清空；失败时保留供下次 Stop 重试。
        /// </summary>
        public static bool Stop()
        {
            GameObject host = _host;
            CaptureHostBehaviour behaviour = _behaviour;

            // 先使旧 generation 失效，阻止新的 capture callback / commit。
            _activeGeneration = 0;

            // 先同步让 behaviour 失效（停止 coroutine、清 callback / pending），再销毁 host。
            if (behaviour != null)
            {
                try
                {
                    behaviour.Shutdown();
                }
                catch (Exception ex)
                {
                    Log.Exception("FrameCaptureDriver: 停止失败，保留 host 供重试", ex);
                    return false;
                }
            }

            // Render Source ownership 必须在 host Destroy 之前精确恢复。
            bool sourceRestored;
            try
            {
                sourceRestored = RestoreCameraSource();
            }
            catch (Exception ex)
            {
                sourceRestored = false;
                Log.Exception("FrameCaptureDriver: 释放 capture source 异常", ex);
            }

            bool hostDestroyed = true;
            if (host != null)
            {
                try
                {
                    UnityEngine.Object.Destroy(host);
                }
                catch (Exception ex)
                {
                    hostDestroyed = false;
                    // 保留静态引用：Shutdown 已幂等失效 behaviour，下一次 Stop 可以再次尝试 Destroy。
                    Log.Exception("FrameCaptureDriver: 销毁 host 失败，保留 ownership 供重试", ex);
                }
            }

            if (hostDestroyed)
            {
                if (ReferenceEquals(_host, host)) _host = null;
                if (ReferenceEquals(_behaviour, behaviour)) _behaviour = null;
            }

            return sourceRestored && hostDestroyed && !IsRunning &&
                   !HasOwnedCaptureTarget && _activeGeneration == 0;
        }

        // ================================================================
        // Render Source ownership 释放
        // ================================================================

        /// <summary>
        /// 幂等释放 Render Source。Renderist 只恢复自己仍然拥有的属性：
        ///
        /// 逐 Camera 检查当前 targetTexture 是否仍然精确等于本 session 的 captureTarget。
        ///   * 是  → Renderist 仍拥有该属性 → 写回 session 前的真实值。
        ///   * 否  → native / 游戏 / 其它合法流程已在 session 期间接管该属性
        ///           （例如 Esc 时 scnEditor.SwitchToEditMode → scrCamera.SetupRTCam(false)
        ///           会把三台摄像机置回 null）。此时**不覆盖**当前值，只记录一次
        ///           "ownership already relinquished"，并把该 Camera 视为已不再由
        ///           Renderist 拥有。值不同不是 cleanup failure。
        ///
        /// 逐 Camera 处理结束后再确认是否仍有 live Camera 精确引用 captureTarget：
        ///   * 有 → restoration 未完成，保留 ownership 与 captureTarget 供下次 retry，
        ///          返回 false（绝不 Release / Destroy 仍被引用的 RenderTexture）。
        ///   * 无 → 尝试 Release + Destroy；两步都成功返回后才清空 captureTarget、Camera
        ///          与冻结尺寸。任一步异常都保留 target 引用并返回 false，供下一次 Stop 重试。
        ///
        /// Camera aspect 与 targetTexture **各自独立**释放（RelinquishAspect /
        /// RelinquishTargetTexture），但收敛判定合并：aspect 恢复失败同样返回 false 并保留
        /// ownership，因此 residual 不会被提前清空；最后一次成功收敛时才清空 aspect 记录。
        /// </summary>
        private static bool RestoreCameraSource()
        {
            if (_sourceActive)
            {
                bool bgStaticReleased = RelinquishTargetTexture(_bgStaticCamera, _oldBgStaticTarget, "Bgcamstatic");
                bool bgReleased = RelinquishTargetTexture(_bgCamera, _oldBgTarget, "BGcam");
                bool mainReleased = RelinquishTargetTexture(_mainCamera, _oldMainTarget, "camobj");

                // aspect ownership：只在**本次确实写过**、且当前值仍等于 Renderist 写入值时才
                // ResetAspect()（恢复 Unity 自动行为）；没写过的、或已被外部流程改写的值都不覆盖。
                bool bgStaticAspectReleased = RelinquishAspect(
                    _bgStaticCamera, _writtenAspect, _oldBgStaticAspect, "Bgcamstatic", _aspectAssignedCount >= 1);
                bool bgAspectReleased = RelinquishAspect(
                    _bgCamera, _writtenAspect, _oldBgAspect, "BGcam", _aspectAssignedCount >= 2);
                bool mainAspectReleased = RelinquishAspect(
                    _mainCamera, _writtenAspect, _oldMainAspect, "camobj", _aspectAssignedCount >= 3);

                bool allHandled = bgStaticReleased & bgReleased & mainReleased &
                                  bgStaticAspectReleased & bgAspectReleased & mainAspectReleased;

                if (!allHandled || IsCaptureTargetStillReferenced())
                {
                    Log.Warn("FrameCaptureDriver: capture source 释放未完成，" +
                             "保留 captureTarget 与 ownership 供下一次 cleanup 重试" +
                             " (writeFailed=" + (!allHandled ? "true" : "false") +
                             " targetTextureReleased=" +
                             ((bgStaticReleased & bgReleased & mainReleased) ? "true" : "false") +
                             " aspectReleased=" +
                             ((bgStaticAspectReleased & bgAspectReleased & mainAspectReleased) ? "true" : "false") +
                             " stillReferenced=" + (IsCaptureTargetStillReferenced() ? "true" : "false") + ")");
                    return false;
                }
                _sourceActive = false;
            }

            // 幂等重试路径：即使 source 未标记 active，也不得销毁仍被任何 live Camera 引用的 RT。
            if (IsCaptureTargetStillReferenced())
            {
                Log.Warn("FrameCaptureDriver: captureTarget 仍被 live Camera 引用，保留 ownership 供重试");
                return false;
            }

            RenderTexture target = _captureTarget;
            if (!TryDestroyTexture(target))
            {
                // target 仍保留在 _captureTarget；下次 Stop 只重试资源销毁，不会重写 Camera。
                return false;
            }

            if (ReferenceEquals(_captureTarget, target))
                _captureTarget = null;

            _bgStaticCamera = null;
            _bgCamera = null;
            _mainCamera = null;
            _oldBgStaticTarget = null;
            _oldBgTarget = null;
            _oldMainTarget = null;
            _writtenAspect = 0f;
            _oldBgStaticAspect = 0f;
            _oldBgAspect = 0f;
            _oldMainAspect = 0f;
            _aspectAssignedCount = 0;
            _captureWidth = 0;
            _captureHeight = 0;
            return true;
        }

        /// <summary>
        /// ownership-aware 单 Camera 释放。只在当前 targetTexture 仍然精确等于
        /// 本 session 的 captureTarget 时才写回 savedOldTarget。
        /// 已销毁的 Camera 视为无需处理。返回 false 仅代表读取/写入本身失败。
        /// </summary>
        private static bool RelinquishTargetTexture(Camera camera, RenderTexture savedOld, string label)
        {
            if (camera == null) return true;

            RenderTexture current;
            try
            {
                current = camera.targetTexture;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 读取 " + label + ".targetTexture 失败", ex);
                return false;
            }

            // current != null 使用 Unity 语义（null 或已销毁均为 false）。
            if (current == null || !ReferenceEquals(current, _captureTarget))
            {
                Log.Info("FrameCaptureDriver: " + label +
                         " target ownership already relinquished / externally changed; " +
                         "leaving current targetTexture untouched (current=" +
                         (current == null ? "null" : "'" + current.name + "'") +
                         " savedOldTarget=" +
                         (savedOld == null ? "null" : "'" + savedOld.name + "'") + ")");
                return true;
            }

            try
            {
                return TryAssignTargetTexture(camera, savedOld, label);
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 恢复 " + label + ".targetTexture 失败", ex);
                return false;
            }
        }

        /// <summary>写入单个 Camera 的 targetTexture；已销毁的 Camera 视为成功的空操作。</summary>
        private static bool TryAssignTargetTexture(Camera camera, RenderTexture value, string label)
        {
            if (camera == null) return true;
            try
            {
                camera.targetTexture = value;
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 写入 " + label + ".targetTexture 失败", ex);
                return false;
            }
        }

        // ================================================================
        // Camera aspect ownership
        // ================================================================

        /// <summary>
        /// 读取单台 Camera 的 aspect baseline。已销毁 / 不可读 / 非有限 / 非正 一律失败：
        /// 激活前无法确定 baseline 就必须 fail-closed，绝不先写入再指望后续恢复。
        /// </summary>
        private static bool TryReadCameraAspect(Camera camera, string label, out float aspect, out string error)
        {
            aspect = 0f;
            error = null;

            if (camera == null)
            {
                error = "capture-aspect-camera-missing:" + label;
                return false;
            }

            float current;
            try
            {
                current = camera.aspect;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: 读取 " + label + ".aspect 失败", ex);
                error = "capture-aspect-unreadable:" + label;
                return false;
            }

            if (float.IsNaN(current) || float.IsInfinity(current) || current <= 0f)
            {
                error = "capture-aspect-baseline-invalid:" + label + "=" +
                        current.ToString("0.######", CultureInfo.InvariantCulture);
                return false;
            }

            aspect = current;
            return true;
        }

        /// <summary>
        /// 三台 baseline aspect 是否互相兼容。
        ///
        /// 判据刻意**不是**「baseline 是否与屏幕 aspect 数值相等」：targetTexture 由游戏接管后，
        /// 自动 aspect 的来源未必是屏幕，用等值推断自动模式会产生假判据（因此本实现不做该推断）。
        /// 这里只要求三台彼此一致——它们共用同一个渲染目标，正常基线必然一致；出现互不一致
        /// 说明存在 Renderist 无法安全统一、也无法在 cleanup 中无损还原的显式 aspect 基线，
        /// 因此 fail-closed。
        /// </summary>
        private static bool IsAspectBaselineCompatible(
            float bgStaticAspect, float bgAspect, float mainAspect, out string detail)
        {
            bool consistent = AspectEquals(bgStaticAspect, bgAspect) &&
                              AspectEquals(bgStaticAspect, mainAspect);
            detail = "Bgcamstatic=" + bgStaticAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                     " BGcam=" + bgAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                     " camobj=" + mainAspect.ToString("0.######", CultureInfo.InvariantCulture);
            return consistent;
        }

        /// <summary>
        /// ownership-aware 单 Camera aspect 释放，语义与 <see cref="RelinquishTargetTexture"/> 一致：
        ///   * 本次从未成功写入该 Camera 的 aspect（<paramref name="aspectWasWritten"/> 为 false，
        ///     例如 setter 在本台之前就抛异常）→ 没有 aspect ownership 需释放，恒为成功空操作；
        ///   * 当前值仍等于 Renderist 写入值 → 仍由 Renderist 拥有 → <c>ResetAspect()</c>
        ///     恢复 Unity 自动行为；
        ///   * 当前值已被外部流程改写 → 只记录，不覆盖，视为已 relinquish（不是 failure）；
        ///   * 读取或 ResetAspect 抛异常 → 返回 false，保留 ownership 供下一次 Stop 重试。
        /// </summary>
        private static bool RelinquishAspect(
            Camera camera, float writtenAspect, float baselineAspect, string label, bool aspectWasWritten)
        {
            if (camera == null) return true;

            // 本 session 从未写入过 aspect（整个写入阶段未开始，或在本台之前失败）：
            // 没有 aspect ownership 需释放。绝不去 ResetAspect 一台没写过的 Camera——
            // 否则在“baseline 数值恰好等于输出 aspect”时会误改外部状态。
            if (!aspectWasWritten || writtenAspect <= 0f) return true;

            float current;
            try
            {
                current = camera.aspect;
            }
            catch (Exception ex)
            {
                // 读不到就保守认为仍由 Renderist 拥有，绝不提前丢弃 ownership。
                Log.Exception("FrameCaptureDriver: 读取 " + label + ".aspect 失败，保留 ownership 供重试", ex);
                return false;
            }

            if (!AspectEquals(current, writtenAspect))
            {
                Log.Info("FrameCaptureDriver: " + label +
                         " aspect ownership already relinquished / externally changed; " +
                         "leaving current aspect untouched (current=" +
                         current.ToString("0.######", CultureInfo.InvariantCulture) +
                         " applied=" + writtenAspect.ToString("0.######", CultureInfo.InvariantCulture) +
                         " baseline=" + baselineAspect.ToString("0.######", CultureInfo.InvariantCulture) + ")");
                return true;
            }

            try
            {
                camera.ResetAspect();
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: ResetAspect(" + label + ") 失败，保留 ownership 供重试", ex);
                return false;
            }
        }

        /// <summary>
        /// setter 成功之后的读回校验，**只用于日志**。
        /// 读回失败或读回值不同都不改变 ownership：ownership 已在第一次 Camera 写入之前登记，
        /// 因此 partial assignment 绝不会因为读回异常而被漏掉。
        /// </summary>
        private static void VerifyAspectAppliedBestEffort(Camera camera, float expected, string label)
        {
            if (camera == null) return;
            try
            {
                float readBack = camera.aspect;
                if (!AspectEquals(readBack, expected))
                {
                    Log.Warn("FrameCaptureDriver: " + label +
                             ".aspect 读回与写入值不同（ownership 已登记，不影响恢复）：readBack=" +
                             readBack.ToString("0.######", CultureInfo.InvariantCulture) +
                             " expected=" + expected.ToString("0.######", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FrameCaptureDriver: " + label +
                         ".aspect 读回失败（ownership 已登记，不影响恢复）：" + ex.Message);
            }
        }

        /// <summary>
        /// aspect 数值等价判定。只吸收浮点表示 / native 往返误差，
        /// 不构成 aspect 合法区间，也不限制任何 aspect 取值。
        /// </summary>
        private static bool AspectEquals(float a, float b)
        {
            float scale = Math.Max(1f, Math.Max(Math.Abs(a), Math.Abs(b)));
            return Math.Abs(a - b) <= AspectTolerance * scale;
        }

        /// <summary>是否仍有任何 live Camera 的 targetTexture 精确引用本次 captureTarget。</summary>
        private static bool IsCaptureTargetStillReferenced()
        {
            if (_captureTarget == null) return false;
            return HoldsCaptureTarget(_bgStaticCamera) ||
                   HoldsCaptureTarget(_bgCamera) ||
                   HoldsCaptureTarget(_mainCamera);
        }

        private static bool HoldsCaptureTarget(Camera camera)
        {
            if (camera == null) return false;
            try
            {
                RenderTexture current = camera.targetTexture;
                return current != null && ReferenceEquals(current, _captureTarget);
            }
            catch
            {
                // 读不到就保守认为仍被引用，避免销毁可能仍被使用的 RenderTexture。
                return true;
            }
        }

        /// <summary>
        /// 释放并销毁 RenderTexture。异常不得吞掉：调用方只有在返回 true 后才可丢弃 ownership。
        /// Release 成功但 Destroy 失败时保留同一引用；下一次 cleanup 重试 Release + Destroy 是幂等的。
        /// </summary>
        private static bool TryDestroyTexture(RenderTexture texture)
        {
            if (texture == null) return true;

            try
            {
                texture.Release();
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: Release capture target 失败", ex);
                return false;
            }

            try
            {
                UnityEngine.Object.Destroy(texture);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("FrameCaptureDriver: Destroy capture target 失败", ex);
                return false;
            }
        }

        private static string DescribeCamera(string label, Camera camera, RenderTexture oldTarget)
        {
            if (camera == null) return label + "=<null>";
            try
            {
                Rect rect = camera.rect;
                return label + "={name='" + camera.name + "'" +
                       " depth=" + camera.depth.ToString(CultureInfo.InvariantCulture) +
                       " enabled=" + (camera.enabled ? "true" : "false") +
                       " activeInHierarchy=" +
                       (camera.gameObject.activeInHierarchy ? "true" : "false") +
                       " clearFlags=" + camera.clearFlags +
                       " cullingMask=" + camera.cullingMask.ToString(CultureInfo.InvariantCulture) +
                       " aspect=" + camera.aspect.ToString("0.######", CultureInfo.InvariantCulture) +
                       " rect=" + rect.x.ToString("0.######", CultureInfo.InvariantCulture) + "," +
                       rect.y.ToString("0.######", CultureInfo.InvariantCulture) + "," +
                       rect.width.ToString("0.######", CultureInfo.InvariantCulture) + "," +
                       rect.height.ToString("0.######", CultureInfo.InvariantCulture) +
                       " oldTargetTexture=" +
                       (oldTarget == null ? "null" : "'" + oldTarget.name + "'") + "}";
            }
            catch (Exception ex)
            {
                return label + "=<read-failed:" + ex.Message + ">";
            }
        }

        // ================================================================
        // MonoBehaviour 宿主
        // ================================================================

        private sealed class CaptureHostBehaviour : MonoBehaviour
        {
            private Coroutine _routine;
            private string _outputDirectory;
            private string _prefix;
            private int _zeroPadWidth;
            private CaptureResultCallback _onResult;
            private long _generation;
            /// <summary>
            /// session 开始时冻结的输出模式。true = PNG 序列；false = log-only
            /// （帧事务照常，但绝不读回 / 编码 / 写盘，也不构造 PNG 文件路径）。
            /// </summary>
            private bool _imageOutputEnabled = true;

            private bool _pending;
            private long _pendingIndex;
            private Texture2D _texture;
            private bool _stopped;

            public void Configure(string outputDirectory, string prefix, int zeroPadWidth,
                CaptureResultCallback onResult, long generation, bool imageOutputEnabled)
            {
                _outputDirectory = outputDirectory;
                _prefix = prefix;
                _zeroPadWidth = zeroPadWidth;
                _onResult = onResult;
                _generation = generation;
                _imageOutputEnabled = imageOutputEnabled;
                _pending = false;
                _pendingIndex = -1;
                _stopped = false;

                if (_routine == null)
                {
                    _routine = StartCoroutine(Observe());
                }
            }

            public void RequestCapture(long frameIndex)
            {
                if (_stopped || _generation != _activeGeneration) return;
                if (!_pending)
                {
                    _pending = true;
                    _pendingIndex = frameIndex;
                }
                // 同帧重复请求只保留首次，避免重复捕获。
            }

            /// <summary>同步失效：停止 coroutine、清 pending / callback。调用后再 Destroy host。</summary>
            public void Shutdown()
            {
                _stopped = true;
                _pending = false;
                _pendingIndex = -1;
                _onResult = null;

                if (_routine != null)
                {
                    try { StopCoroutine(_routine); } catch { }
                    _routine = null;
                }
            }

            private IEnumerator Observe()
            {
                while (true)
                {
                    yield return new WaitForEndOfFrame();
                    if (_stopped || _generation != _activeGeneration) yield break;
                    if (!_pending) continue;

                    long index = _pendingIndex;
                    _pending = false;
                    if (index == 0) Log.Info("MasterTimeline Stage=Frame0 AFTER_EOF frameIndex=0");
                    _pendingIndex = -1;
                    CaptureNow(index);
                }
            }

            private void CaptureNow(long frameIndex)
            {
                if (_stopped || _generation != _activeGeneration) return;

                // Log-only 绝不构造 PNG 文件路径；filePath 保持 null。
                string filePath = _imageOutputEnabled ? BuildFilePath(frameIndex) : null;

                try
                {
                    // 必须从已经激活的 capture source 读取。source 未激活时不得回退到屏幕。
                    // source / 尺寸校验是两种模式共用的帧末事务前置条件。
                    RenderTexture target = _captureTarget;
                    if (!_sourceActive || target == null)
                    {
                        throw new InvalidOperationException("capture-source-inactive");
                    }
                    if (_captureWidth <= 0 || _captureHeight <= 0)
                    {
                        throw new InvalidOperationException("capture-dimensions-invalid");
                    }

                    // ---- Log-only：跳过图像读回 / 编码 / 写盘 ----
                    // 帧末事务已经成立（source 有效、generation 未失效、pending index 由 observe
                    // 循环校验），因此返回成功的帧末结果，由 scheduler 走同一个 CommitFrame。
                    // 本分支不得触碰 EnsureTexture / Texture2D / ReadPixels / Apply /
                    // EncodeToPNG / File.WriteAllBytes。
                    if (!_imageOutputEnabled)
                    {
                        if (_stopped || _generation != _activeGeneration) return;
                        Log.Debug("FrameCaptureDriver: log-only frame-end transaction frameIndex=" +
                                  frameIndex.ToString(CultureInfo.InvariantCulture) + " (no image output)");
                        _onResult?.Invoke(_generation, frameIndex, true, false, null, null);
                        return;
                    }

                    // ---- PNG ----（以下全部只为 image output 服务）
                    EnsureTexture();

                    // RenderTexture.active 只在这段临界区内改变；即使后续
                    // EncodeToPNG / 文件 IO 抛异常，全局 active RT 也已恢复。
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = target;
                    try
                    {
                        _texture.ReadPixels(
                            new Rect(0, 0, _captureWidth, _captureHeight), 0, 0);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                    }

                    if (_stopped || _generation != _activeGeneration) return;

                    _texture.Apply(false);

                    byte[] png = _texture.EncodeToPNG();
                    if (png == null || png.Length == 0)
                    {
                        throw new InvalidOperationException("encode-to-png-empty");
                    }

                    if (_stopped || _generation != _activeGeneration) return;

                    File.WriteAllBytes(filePath, png);

                    if (_stopped || _generation != _activeGeneration) return;

                    Log.Debug("FrameCaptureDriver: wrote " + filePath + " (" + png.Length + " bytes)");
                    _onResult?.Invoke(_generation, frameIndex, true, true, filePath, null);
                }
                catch (Exception ex)
                {
                    Log.Exception("FrameCaptureDriver: 输出帧 " + frameIndex + " 帧末事务失败", ex);
                    _onResult?.Invoke(_generation, frameIndex, false, false, filePath, ex.Message);
                }
            }

            private void EnsureTexture()
            {
                // 必须基于本 session 冻结的 capture 尺寸，而不是每帧读取 Screen。
                if (_texture != null && _texture.width == _captureWidth && _texture.height == _captureHeight)
                {
                    return;
                }

                if (_texture != null)
                {
                    try { UnityEngine.Object.Destroy(_texture); } catch { }
                }

                // RGB24：只读 CPU 纹理，EncodeToPNG 前不触发 GPU 上传依赖。
                _texture = new Texture2D(_captureWidth, _captureHeight, TextureFormat.RGB24, false);
            }

            /// <summary>
            /// frame_&lt;index&gt;.png。index 是 long：ZeroPadWidth 只是**最小**补零宽度，
            /// 位数超过它时自然扩展（PadLeft 只补不截），因此不存在编号截断，
            /// 也绝不把 index 转回 int。
            /// </summary>
            private string BuildFilePath(long frameIndex)
            {
                string indexText = frameIndex
                    .ToString(CultureInfo.InvariantCulture)
                    .PadLeft(_zeroPadWidth, '0');
                string filename = _prefix + indexText + ".png";
                return Path.Combine(_outputDirectory, filename);
            }

            private void OnDestroy()
            {
                if (_routine != null)
                {
                    try { StopCoroutine(_routine); } catch { }
                    _routine = null;
                }

                if (_texture != null)
                {
                    try { UnityEngine.Object.Destroy(_texture); } catch { }
                    _texture = null;
                }
            }
        }
    }
}
