"""Generate JSON with the actual .7 pure source, then restore with current pure source."""
import argparse, hashlib, json, subprocess
from pathlib import Path
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[3]
BASE = '35ef09b13dddb2d581582689e34978b9ac285d39'
FILES = ['CManualInspectionModels.cs', 'CManualInspectionOffsetBundle.cs', 'CManualInspectionWorkspace.cs']
COMMON = ROOT / 'Drilling.Common/Review/ManualInspection'
PROGRAM = r'''
using System.Text.Json;
using Drilling.Common.Review.ManualInspection;
var root=args[0];Directory.CreateDirectory(root);
var context=new ST_MANUAL_CONTEXT("SYNTHETIC_RECIPE","SYNTHETIC_RUN","REV0","SYNTHETIC_GEOMETRY");
ST_MANUAL_POINT P(int n)=>new($"C1/H1/S{n}/B11",1,1,n,11,n*.01,0){Row=0,Column=n,Judge="OK",Source="MEASURED",ErrorXUm=3,ErrorYUm=1,
 OriginalMeasurement=new("OK",3,1,$"SYNTHETIC_RAW_{n}",context.RecipeId,context.RunId,context.CoordinateSignature){MeasuredAt=DateTimeOffset.Parse("2026-11-24T00:00:00Z")}};
var points=new[]{P(1),P(2)};var values=points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(10,-2));
CManualInspectionWorkspace W(){var w=new CManualInspectionWorkspace();w.Load(context,points,values,new(30,200,5),(_,_,_)=>new(true,"synthetic field"));return w;}
var results=new List<object>();
foreach(var mode in new[]{"proxy","operator","model-import","saved-proxy","saved-operator"})
{
 var path=Path.Combine(root,mode+".json");var w=W();
#if GENERATE
 if(mode.EndsWith("proxy"))w.ConfirmReferenceCopy(w.PreviewReferenceCopy(points[0].Key,[points[1].Key]),[points[1].Key],"synthetic legacy copy");
 else if(mode.EndsWith("operator")){w.RecordOperatorResults([points[0].Key],3,1,"synthetic legacy operator");w.BuildDirectCandidate([points[0].Key]);}
 else w.BuildOffsetReplacementCandidate(points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(7,2)),"synthetic legacy full model",false);
 if(mode.StartsWith("saved-")){if(!w.ValidateCandidate(true).Ok)throw new Exception("old save validation");w.CommitApplied(w.CreateApplySnapshot(true),"LEGACY_SAVED","synthetic old applied");}
 File.WriteAllText(path,w.ExportSessionJson());
 results.Add(new{mode,generated=true,oldCandidateRows=w.Candidate!.Rows.Count,oldPolicyIsAccumulationOrFullModel=true});
#else
 var saved=mode.StartsWith("saved-");var expectedValues=values.ToDictionary(p=>p.Key,p=>p.Value);
 var expectedContext=saved?context with{RevisionId="LEGACY_SAVED"}:context;
 var subject=mode.EndsWith("proxy")?points[1].Key:points[0].Key;
 if(saved)expectedValues[subject]=new(13,-1);
 var actualGeometry=saved?points.Select(p=>p with{HasAppliedOffsetEvidence=true}).ToArray():points;
 w.RestoreSessionJson(File.ReadAllText(path),expectedContext,actualGeometry,expectedValues,(_,_,_)=>new(true,"synthetic field"));
 void Assert(bool ok,string why){if(!ok)throw new InvalidOperationException(mode+": "+why);results.Add(new{mode,check=why,passed=true});}
 Assert(w.Context!.LineageValid,"legacy proof and raw lineage survived real old serializer");
 Assert(w.Points.All(p=>p.OriginalMeasurement!.RawToken.StartsWith("SYNTHETIC_RAW_")),"original raw preserved");
 Assert(w.ActiveOffsets.All(p=>p.Value==expectedValues[p.Key]),"old saved base preserved on restore");
 Assert(w.Candidate is not null&&!w.Candidate.IsMeasurementReplacement&&w.Candidate.ReplacementCellNo is null,"old candidate not silently reclassified");
 if(saved)
 {
  Assert(w.PreviewMeasurementReplacement([subject]).Keys.Count==0,"already applied legacy manual result not silently replaced");
  var reference=points.Single(p=>p.Key!=subject).Key;var copy=w.PreviewMeasurementReferenceCopy(reference,[subject]);
  w.ConfirmMeasurementReferenceCopy(copy,[subject],"explicit fresh copy after legacy save");
  Assert(w.PreviewMeasurementReplacement([subject]).Keys.SequenceEqual([subject]),"explicit fresh copy can be applied under new policy");
 }
 else if(mode=="model-import")
 {
  Assert(w.HasPendingOffsetEdit,"old model draft remains explicit");w.CancelOffsetReplacement();
  w.BuildScopedOffsetReplacementCandidate(1,points.ToDictionary(p=>p.Key,_=>new ST_MANUAL_OFFSET(7,2)),new HashSet<string>(),"restaged Cell");
  Assert(w.Candidate!.ReplacementCellNo==1,"explicit current Cell restage required");
 }
 else
 {
  var key=mode=="proxy"?points[1].Key:points[0].Key;
  Assert(w.ResolveMeasurementReferenceValue(key).Final==new ST_MANUAL_OFFSET(3,1),"old captured final13 is not reused as deviation");
  w.BuildMeasurementReplacementCandidate([key]);Assert(w.ValidateCandidate(mode=="proxy"||mode=="operator").Ok,"new replacement validates against restored provenance");
  var snapshot=w.CreateApplySnapshot(true);w.CommitApplied(snapshot,"SYNTHETIC_REPLACED","synthetic no-device commit");
  Assert(w.ActiveOffsets[key]==new ST_MANUAL_OFFSET(3,1),"new final is raw3 not old13 or repeated23");
 }
#endif
}
File.WriteAllText(Path.Combine(root,
#if GENERATE
 "generated.json"
#else
 "verified.json"
#endif
 ),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("No-device legacy serialization checks: "+results.Count);
'''

def run(command, cwd, log):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log.write_bytes(result.stdout)
    if result.returncode:
        print(result.stdout.decode('utf-8', errors='replace')[-5000:])
        raise RuntimeError('Command failed: ' + str(command))

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output', required=True)
    args = p.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    sources = {}
    for name in FILES:
        rel = 'Drilling.Common/Review/ManualInspection/' + name
        data = subprocess.check_output(['git', 'show', BASE + ':' + rel], cwd=ROOT)
        target = output / 'old-source' / name
        target.parent.mkdir(exist_ok=True)
        target.write_bytes(data)
        sources[rel] = {'oldGitSha256': hashlib.sha256(data).hexdigest(), 'currentSha256': hashlib.sha256((COMMON / name).read_bytes()).hexdigest()}
    for mode in ['generate', 'verify']:
        directory = output / mode
        directory.mkdir(exist_ok=True)
        (directory / 'Program.cs').write_text(PROGRAM, encoding='utf-8')
        source = output / 'old-source' if mode == 'generate' else COMMON
        items = ''.join('<Compile Include="' + escape(str(source / name)) + '" />' for name in FILES)
        (directory / 'Harness.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>' + ('<DefineConstants>GENERATE</DefineConstants>' if mode == 'generate' else '') + '</PropertyGroup><ItemGroup>' + items + '</ItemGroup></Project>', encoding='utf-8')
        run(['dotnet', 'run', '--project', str(directory / 'Harness.csproj'), '-c', 'Release', '--', str(output / 'fixtures')], ROOT, output / (mode + '.log'))
    checks = json.loads((output / 'fixtures/verified.json').read_text())
    report = {'passed': all(c['passed'] for c in checks), 'count': len(checks), 'checks': checks, 'legacySourceCommit': BASE, 'sourceSha256': sources,
              'scope': 'Actual git-show .7 pure source serializer -> current pure source restorer; synthetic data only; no production device App/SDK/driver.'}
    (output.parent / 'legacy-serialization.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': report['passed'], 'count': len(checks)}))

if __name__ == '__main__':
    main()
