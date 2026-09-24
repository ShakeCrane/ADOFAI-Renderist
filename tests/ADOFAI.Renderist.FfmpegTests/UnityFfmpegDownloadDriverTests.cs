using System;
using System.IO;
using System.Threading;
using ADOFAI.Renderist.Ffmpeg;
using UnityEngine.Networking;

namespace ADOFAI.Renderist.FfmpegTests
{
    internal static class UnityFfmpegDownloadDriverTests
    {
        public static void Run(string workRoot)
        {
            TestKit.Run("unity driver: completed request reaches controller after dispose", () =>
            {
                string work = TestKit.NewWorkDirectory(workRoot, "driver-complete");
                string archive;
                FfmpegAsset asset = Fixtures.BuildSyntheticAssetWithFakeFfmpeg(work, out archive);
                FfmpegInstallLayout layout;
                string layoutError;
                TestKit.Check(FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"),
                    out layout, out layoutError), layoutError);

                var driver = new UnityFfmpegDownloadDriver();
                var controller = new FfmpegDownloadController(layout, driver.AbortAndDispose);
                FfmpegDownloadPlan plan = controller.TryStart(asset, true, 30);
                File.Copy(archive, plan.TempFilePath);

                int completions = 0;
                bool disposedBeforeCallback = false;
                string startError;
                string startDetail;
                TestKit.Check(driver.TryStart(plan,
                    (generation, received, total) => controller.ReportProgress(received, total),
                    (generation, response) =>
                    {
                        completions++;
                        disposedBeforeCallback = UnityWebRequest.Last.Disposed;
                        TestKit.Check(controller.ReportFinished(generation, response), "current response accepted");
                    }, out startError, out startDetail), startError + ": " + startDetail);

                UnityWebRequest request = UnityWebRequest.Last;
                request.downloadedBytes = (ulong)asset.ArchiveSizeBytes;
                request.ContentLength = asset.ArchiveSizeBytes.ToString();
                driver.Pump();
                TestKit.CheckEqual(FfmpegDownloadState.Downloading, controller.State, "incomplete request state");
                TestKit.CheckEqual(asset.ArchiveSizeBytes, controller.DownloadedBytes, "progress");

                request.responseCode = 200;
                request.result = UnityWebRequest.Result.Success;
                request.isDone = true;
                request.Operation.Done = true;
                driver.Pump();
                driver.Pump();

                TestKit.CheckEqual(1, completions, "one completion delivery");
                TestKit.Check(disposedBeforeCallback, "file handle disposed before controller starts verification");
                TestKit.Check(!request.Aborted, "successful request must not be aborted");
                TestKit.Check(!driver.IsActive, "driver released");
                TestKit.CheckEqual(FfmpegDownloadState.Verifying, controller.State, "controller entered verification");

                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (controller.IsBusy && DateTime.UtcNow < deadline)
                {
                    controller.Pump();
                    Thread.Sleep(5);
                }
                TestKit.CheckEqual(FfmpegDownloadState.Succeeded, controller.State,
                    "verified installation: " + controller.ErrorCode + " " + controller.ErrorDetail);
            });

            TestKit.Run("unity driver: request failure reaches controller and cleans file", () =>
            {
                string work = TestKit.NewWorkDirectory(workRoot, "driver-error");
                string archive;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archive);
                FfmpegInstallLayout layout;
                string layoutError;
                TestKit.Check(FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"),
                    out layout, out layoutError), layoutError);
                var driver = new UnityFfmpegDownloadDriver();
                var controller = new FfmpegDownloadController(layout, driver.AbortAndDispose);
                FfmpegDownloadPlan plan = controller.TryStart(asset, false, 5);
                File.Copy(archive, plan.TempFilePath);
                string startError;
                string startDetail;
                TestKit.Check(driver.TryStart(plan, null,
                    (generation, response) => controller.ReportFinished(generation, response),
                    out startError, out startDetail), startError + ": " + startDetail);
                UnityWebRequest request = UnityWebRequest.Last;
                request.result = UnityWebRequest.Result.ConnectionError;
                request.error = "transport failed";
                request.Operation.Done = true;
                request.isDone = true;
                driver.Pump();
                TestKit.CheckEqual(FfmpegDownloadState.Failed, controller.State, "state");
                TestKit.CheckEqual("download-request-failed", controller.ErrorCode, "error code");
                TestKit.Check(!File.Exists(plan.TempFilePath), "owned temp file deleted");
            });

            TestKit.Run("unity driver: state-read exception is delivered as failure", () =>
            {
                var driver = new UnityFfmpegDownloadDriver();
                var plan = new FfmpegDownloadPlan
                {
                    Started = true, Generation = 7, Url = "https://example.invalid/fixed.zip",
                    TempFilePath = Path.Combine(workRoot, "unused.zip"),
                };
                int deliveries = 0;
                FfmpegDownloadResponse received = null;
                string startError;
                string startDetail;
                TestKit.Check(driver.TryStart(plan, null,
                    (generation, response) => { deliveries++; received = response; },
                    out startError, out startDetail), startError + ": " + startDetail);
                UnityWebRequest.Last.Operation.ThrowOnRead = true;
                driver.Pump();
                driver.Pump();
                TestKit.CheckEqual(1, deliveries, "one failure delivery");
                TestKit.Check(received != null && !received.RequestSucceeded, "failure response");
                TestKit.Check(received.Error.StartsWith("download-driver-exception", StringComparison.Ordinal),
                    "exception detail preserved");
            });

            TestKit.Run("unity driver: cancel aborts old generation and allows new request", () =>
            {
                string work = TestKit.NewWorkDirectory(workRoot, "driver-cancel");
                string archive;
                FfmpegAsset asset = Fixtures.BuildSyntheticAsset(work, out archive);
                FfmpegInstallLayout layout;
                string layoutError;
                TestKit.Check(FfmpegInstallLayout.TryCreate(Path.Combine(work, "managed"),
                    out layout, out layoutError), layoutError);
                var driver = new UnityFfmpegDownloadDriver();
                var controller = new FfmpegDownloadController(layout, driver.AbortAndDispose);
                FfmpegDownloadPlan first = controller.TryStart(asset, false, 5);
                int deliveries = 0;
                string startError;
                string startDetail;
                TestKit.Check(driver.TryStart(first, null,
                    (generation, response) => deliveries++, out startError, out startDetail),
                    startError + ": " + startDetail);
                UnityWebRequest oldRequest = UnityWebRequest.Last;
                controller.Cancel("test");
                TestKit.Check(oldRequest.Aborted && oldRequest.Disposed, "old request aborted and disposed");
                TestKit.Check(!driver.IsActive, "old driver request released");
                TestKit.CheckEqual(0, deliveries, "cancel must not deliver completion");

                FfmpegDownloadPlan next = controller.TryStart(asset, false, 5);
                TestKit.Check(next.Started && next.Generation != first.Generation, "new generation started");
                TestKit.Check(driver.TryStart(next, null,
                    (generation, response) => deliveries++, out startError, out startDetail),
                    startError + ": " + startDetail);
                driver.AbortAndDispose(first.Generation);
                TestKit.Check(driver.IsActive, "old abort cannot stop new request");
                controller.Cancel("test-cleanup");
            });
        }
    }
}
