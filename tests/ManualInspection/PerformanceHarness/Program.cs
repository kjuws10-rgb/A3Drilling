using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Drilling.Common.Review.ManualInspection;
using Drilling.UI.Menu.Menus;
using Drilling.UI.Views;

// Real production Core/Controller/serialization, synthetic immediate async backend.
// No production App, Root, Manager, device, SDK, movement or CSV writer is constructed.
internal static class Program
{
    private static readonly List<object> Cases=[];
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    private static bool ForceCheckpointDue;
    [STAThread]
    private static int Main(string[] args)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var exit=1;
        app.Dispatcher.BeginInvoke(async()=>{try{exit=await Run(args);}finally{app.Shutdown(exit);}});
        app.Run();return exit;
    }
    private static async Task<int> Run(string[] args)
    {
        var path=args.Length>0?Path.GetFullPath(args[0]):Path.Combine(AppContext.BaseDirectory,"performance.json");
        var sizes=args.Length>1?args[1].Split(',').Select(int.Parse).ToArray():[1000,10000,20000,32000];
        var iterations=args.Length>2?int.Parse(args[2]):3;
        ForceCheckpointDue=args.Contains("--checkpoint-due",StringComparer.Ordinal);
        var failed=0;
        try
        {
            // JIT/serializer warm-up is separate from the reported model sizes and samples.
            await RunSize(64,1,warmup:true);
            foreach(var count in sizes)await RunSize(count,iterations,warmup:false);
        }
        catch(Exception error){failed++;Cases.Add(new{kind="fatal",passed=false,error=error.ToString()});}
        var sourceRoot=Environment.GetEnvironmentVariable("A3_BENCH_SOURCE_ROOT");
        var sourceFiles=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!string.IsNullOrEmpty(sourceRoot))foreach(var name in new[]{"Drilling.Common/Review/ManualInspection/CManualInspectionWorkspace.cs","Drilling.Common/Review/ManualInspection/CManualInspectionModels.cs","Drilling.UI/Menu/Menus/CManualInspectionController.cs","Drilling.UI/Views/CManualInspectionUiContract.cs","tests/ManualInspection/ControllerHarness/Contracts.cs"})
            sourceFiles[name]=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(sourceRoot,name)))).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new
        {
            schema="a3-manual-inspection-controller-performance/1",passed=failed==0,generatedAtUtc=DateTimeOffset.UtcNow,
            sizes,iterations,checkpointDueDiagnostic=ForceCheckpointDue,measurements=Cases,sourceFiles,
            assemblySha256=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(CManualInspectionController).Assembly.Location))).ToLowerInvariant(),
            runtime=Environment.Version.ToString(),process64Bit=Environment.Is64BitProcess,
            scope="Source-linked actual Core/Controller plus actual checkpoint JSON/temp-file I/O. Generic STA WPF Application with a coalesced 10 ms Input dispatcher heartbeat. Synthetic async no-device backend uses Task.Yield, never artificial sleep. This excludes native Recipe/Setting/Review CSV I/O, real Vision latency, physical motion and production UIView rendering.",
            cautions=new[]{"Wall time includes real local temp-file I/O and GC. Scheduler/disk noise remains; compare repeated same-size samples.","No queued heartbeat sample does not prove a sub-millisecond UI stall. Longest dispatch gap includes measurement start/end boundaries.","Source-only and actual-assembly correctness suites are separate; timing samples must not be added to their case totals.","When checkpointDueDiagnostic=true, the fake response ages only the Controller checkpoint timestamp immediately before returning. This is a scheduling-state diagnostic without sleeping, not a real Vision time or normal speed-improvement sample."}
        },JsonOptions));
        Console.WriteLine($"Performance report: {path}; passed={failed==0}");return failed==0?0:1;
    }
    private static async Task RunSize(int count,int iterations,bool warmup)
    {
        var backend=new ImmediateBackend(count);var controller=new CManualInspectionController(backend);
        if(ForceCheckpointDue)backend.BeforeReturn=()=>typeof(CManualInspectionController).GetField("_lastCheckpoint",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(controller,DateTimeOffset.UtcNow.AddSeconds(-3));
        var publications=0;var pointsRebuilt=0;object? oldPoints=null;
        controller.Changed+=(_,_)=>{publications++;if(!ReferenceEquals(oldPoints,controller.Snapshot.Points)){pointsRebuilt++;oldPoints=controller.Snapshot.Points;}};
        var loaded=await Timed("load",()=>controller.EnsureLoaded());
        Require(controller.Snapshot.Points.Count==count&&!controller.Snapshot.IsBusy,"loaded point population");
        if(!warmup)Cases.Add(new{kind="load",count,timing=loaded,publications,pointsRebuilt});
        for(var iteration=0;iteration<iterations;iteration++)
        {
            var publicationsBefore=publications;var pointsBefore=pointsRebuilt;
            var result=await Timed("Measure",()=>controller.ExecuteAsync(EN_MANUAL_INSPECTION_UI_ACTION.Measure,new(){ExpectedVersion=controller.Snapshot.Version,Keys=["P1"],CurrentCellNo=1}));
            var point=controller.Snapshot.Points.Single(p=>p.Key=="P1");
            Require(point.OriginalXUm==2.25&&point.OriginalYUm==-1.5&&point.WorkingJudge=="OK"&&!controller.IsBusy&&!controller.Snapshot.HasIncomplete,"measurement/raw/completion");
            Require(backend.Saves==0&&backend.MotionCalls==0&&backend.Measures==iteration+1,"no implicit movement or offset save");
            Require(controller.Snapshot.Points.Where(p=>p.Key!="P1").All(p=>p.OriginalXUm is null&&p.BaseXUm==0),"unrelated points remain absent and baseline remains zero");
            if(!warmup)Cases.Add(new{kind=ForceCheckpointDue?"current_position_measure_checkpoint_due":"current_position_measure",count,iteration=iteration+1,timing=result,publications=publications-publicationsBefore,pointsRebuilt=pointsRebuilt-pointsBefore,backendCurrentChecks=backend.CurrentChecks,backendMeasures=backend.Measures,backendSaves=backend.Saves,passed=true});
            Console.WriteLine($"{(warmup?"warmup":"sample")} n={count} i={iteration+1}: {result.WallMs:0.0} ms, UI dispatch gap {result.MaxDispatcherGapMs:0.0} ms, total alloc {result.TotalAllocatedBytes:N0}");
        }
        Require(Directory.GetFiles(backend.StateDirectory,"*.json",SearchOption.AllDirectories).Length>0,"actual checkpoint JSON written");
    }
    private static async Task<Sample> Timed(string name,Func<Task> action)
    {
        // Synchronize the preceding operation before starting the measured interval.
        await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        using var heartbeat=new Heartbeat(Application.Current.Dispatcher);
        var gc0=GC.CollectionCount(0);var gc1=GC.CollectionCount(1);var gc2=GC.CollectionCount(2);
        var beforeAll=GC.GetTotalAllocatedBytes(precise:true);var beforeUi=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
        heartbeat.Start();await action();await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Input);heartbeat.Finish();watch.Stop();
        return new(name,watch.Elapsed.TotalMilliseconds,heartbeat.MaxGapMs,heartbeat.MaxQueueDelayMs,heartbeat.Pulses,GC.GetTotalAllocatedBytes(true)-beforeAll,GC.GetAllocatedBytesForCurrentThread()-beforeUi,GC.CollectionCount(0)-gc0,GC.CollectionCount(1)-gc1,GC.CollectionCount(2)-gc2);
    }
    private static void Require(bool value,string detail){if(!value)throw new InvalidOperationException("Benchmark state check failed: "+detail);}
    private sealed record Sample(string Phase,double WallMs,double MaxDispatcherGapMs,double MaxDispatcherQueueDelayMs,int HeartbeatPulses,long TotalAllocatedBytes,long UiThreadAllocatedBytes,int Gen0,int Gen1,int Gen2);
    private sealed class Heartbeat(Dispatcher dispatcher):IDisposable
    {
        private System.Threading.Timer? timer;private int pending;private volatile bool stopped;private long last;private double gap,delay;
        public double MaxGapMs=>gap;public double MaxQueueDelayMs=>delay;public int Pulses{get;private set;}
        private static double Ms(long ticks)=>ticks*1000d/Stopwatch.Frequency;
        public void Start(){last=Stopwatch.GetTimestamp();timer=new(_=>Queue(),null,10,10);}
        private void Queue()
        {
            if(stopped||Interlocked.CompareExchange(ref pending,1,0)!=0)return;var sent=Stopwatch.GetTimestamp();
            dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
            {if(!stopped){var now=Stopwatch.GetTimestamp();delay=Math.Max(delay,Ms(now-sent));gap=Math.Max(gap,Ms(now-last));last=now;Pulses++;}Interlocked.Exchange(ref pending,0);}));
        }
        public void Finish(){var now=Stopwatch.GetTimestamp();gap=Math.Max(gap,Ms(now-last));stopped=true;timer?.Dispose();}
        public void Dispose(){stopped=true;timer?.Dispose();}
    }
    private sealed class ImmediateBackend:IManualInspectionBackend
    {
        private readonly CManualInspectionSource source;public int CurrentChecks,Measures,MotionCalls,Saves;
        public Action? BeforeReturn;
        public ImmediateBackend(int count)
        {
            StateDirectory=Path.Combine(Path.GetTempPath(),"A3Performance",Guid.NewGuid().ToString("N"));
            var points=Enumerable.Range(0,count).Select(i=>new ST_MANUAL_POINT("P"+(i+1),i%45+1,1,i/45+1,11,(i/45%100)*.1,(i/4500)*.1){Row=i/4500,Column=i/45%100,Judge="UNMEASURED",Source="KEPT",IsMeasurementOffsetInitialized=true}).ToArray();
            source=new(new("PERF-SYNTHETIC","SYNTHETIC-RUN","INITIAL_ZERO","SYNTHETIC-COORD"),points,points.ToDictionary(p=>p.Key,_=>ST_MANUAL_OFFSET.Zero),new(30,200,5),(_,_,_)=>new(true,"synthetic no-device field"),5,5,"","Synthetic performance population")
            {StoredInitializedOffsetKeys=points.Select(p=>p.OffsetKey).ToHashSet(StringComparer.Ordinal)};
        }
        public bool IsSimulation=>false;public string StateDirectory{get;}
        public Task<CManualInspectionSource> LoadAsync(string? path,CancellationToken token,IReadOnlyList<string>? displayKeys=null){token.ThrowIfCancellationRequested();return Task.FromResult(source);}
        public async Task EnsureCurrentAsync(CManualInspectionSource s,CancellationToken token){CurrentChecks++;await Task.Yield();token.ThrowIfCancellationRequested();Require(s.Context==source.Context,"same synthetic native context");}
        public void GuardOperation(){}
        public async Task<CManualInspectionMeasurement> ExecutePointAsync(CManualInspectionSource s,string key,EN_MANUAL_OPERATION_KIND kind,Action<string> progress,CancellationToken token)
        {
            if(kind!=EN_MANUAL_OPERATION_KIND.Measure){MotionCalls++;throw new InvalidOperationException("Benchmark forbids movement");}
            progress("Synthetic Vision response pending; no device");await Task.Yield();token.ThrowIfCancellationRequested();Measures++;
            var p=s.Points.First(p=>p.Key==key);BeforeReturn?.Invoke();return new(key,new("OK",2.25,-1.5,"PERF-RAW-"+Measures,s.Context.RecipeId,s.Context.RunId,s.Context.CoordinateSignature){RawData="Synthetic observation "+Measures},p.XMm,p.YMm);
        }
        public Task ExecuteFlyingAsync(CManualInspectionSource s,IReadOnlyList<string> keys,Action<CManualInspectionMeasurement> measured,Action<string> progress,CancellationToken token)=>throw new InvalidOperationException("Benchmark forbids flying");
        public Task<string> SaveAsync(CManualInspectionSource s,IReadOnlyDictionary<string,ST_MANUAL_OFFSET> values,string identity,IReadOnlyList<ST_MANUAL_CANDIDATE_ROW> rows,CancellationToken token,IReadOnlyDictionary<string,string>? consumed=null){Saves++;throw new InvalidOperationException("Benchmark forbids offset saving");}
        public void Stop(){}
    }
}
