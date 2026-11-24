using System.Text.Json;
using Drilling.Common.Review.ManualInspection;

var checks=new List<object>();var failures=0;
void Check(string name,bool ok){checks.Add(new{name,passed=ok});if(!ok)failures++;}
bool Same(ST_MANUAL_OFFSET a,ST_MANUAL_OFFSET b)=>Math.Abs(a.XUm-b.XUm)<1e-8&&Math.Abs(a.YUm-b.YUm)<1e-8;
ST_MANUAL_POINT Point(ST_MANUAL_CONTEXT c,int shot,double x,double y,string raw,string judge="OK")
 =>new($"C1/H1/S{shot}/B11",1,1,shot,11,shot*.01,0)
 {Source="MEASURED",Judge=judge,ErrorXUm=x,ErrorYUm=y,
  OriginalMeasurement=new(judge,x,y,raw,c.RecipeId,c.RunId,c.CoordinateSignature)};
CManualInspectionWorkspace Load(ST_MANUAL_CONTEXT c,ST_MANUAL_POINT[] p,Dictionary<string,ST_MANUAL_OFFSET> b)
 {var w=new CManualInspectionWorkspace();w.Load(c,p,b,new(30,200,5),(_,_,_)=>new(true,"synthetic field only"));return w;}
void Commit(CManualInspectionWorkspace w,string revision,bool allowManual=false)
 {var v=w.ValidateCandidate(allowManual);if(!v.Ok)throw new Exception(string.Join(";",v.Errors));var s=w.CreateApplySnapshot(allowManual);w.CommitApplied(s,revision,"synthetic core-only write");}

// Each cycle represents NEW machining under the current B, not repeated measurement of one hole.
// Unit response and Scale100% are assumptions. This does not prove real device sign or precision.
var needed=new ST_MANUAL_OFFSET(12,-4);var baseline=new ST_MANUAL_OFFSET(10,-2);
for(var cycle=1;cycle<=4;cycle++)
{
 var c=new ST_MANUAL_CONTEXT("SYNTHETIC_RECIPE","FRESH_MACHINING_"+cycle,"BASE_"+cycle,"SYNTHETIC_COORDS");
 var p=Point(c,1,needed.XUm-baseline.XUm,needed.YUm-baseline.YUm,"FRESH_RAW_"+cycle);
 var w=Load(c,[p],new(){{p.Key,baseline}});w.BuildMeasurementReplacementCandidate([p.Key]);
 Check($"cycle{cycle}_actual_core_candidate_valid",w.ValidateCandidate(false).Ok);
 Commit(w,"SAVED_"+cycle);baseline=w.ActiveOffsets[p.Key];
 Check($"cycle{cycle}_unit_plant_next_run_residual_zero",Same(baseline,needed));
 Check($"cycle{cycle}_same_raw_not_added_twice",w.PreviewMeasurementReplacement().Keys.Count==0);
}

var context=new ST_MANUAL_CONTEXT("SYNTHETIC_RECIPE","COPY_RUN","BASE_COPY","SYNTHETIC_COORDS");
var source=Point(context,1,3,1,"SOURCE_RAW");var target=Point(context,2,0,0,"TARGET_OLD_RAW","NG");
var workspace=Load(context,[source,target],new(){{source.Key,new(10,-2)},{target.Key,new(20,5)}});
var preview=workspace.PreviewMeasurementReferenceCopy(source.Key,[target.Key]);
workspace.ConfirmMeasurementReferenceCopy(preview,[target.Key],"same residual to different target base");
Check("copy_only_stages_no_active_change",Same(workspace.ActiveOffsets[target.Key],new(20,5))&&workspace.Candidate is null);
workspace.BuildMeasurementReplacementCandidate([target.Key]);
Check("copied_residual_3_1_adds_to_target_20_5",Same(workspace.Candidate!.Rows.Single().Final,new(23,6)));
Commit(workspace,"COPY_SAVED",true);
Check("copy_neither_replaces_with_source_final_nor_adds_source_base",Same(workspace.ActiveOffsets[target.Key],new(23,6)));
Check("copy_preserves_original_target_ng_raw",workspace.GetPoint(target.Key).OriginalMeasurement==target.OriginalMeasurement);
Check("copy_does_not_save_source_implicitly",Same(workspace.ActiveOffsets[source.Key],new(10,-2)));
Check("saved_copy_not_new_increment",workspace.PreviewMeasurementReplacement([target.Key]).Keys.Count==0);
try
{
 var again=workspace.PreviewMeasurementReferenceCopy(source.Key,[target.Key]);
 workspace.ConfirmMeasurementReferenceCopy(again,[target.Key],"repeated same raw");
 var eligible=workspace.PreviewMeasurementReplacement([target.Key]);
 if(eligible.Keys.Count>0){workspace.BuildMeasurementReplacementCandidate(eligible.Keys);Check("recopy_same_source_raw_cannot_accumulate",!workspace.ValidateCandidate(true).Ok);}
 else Check("recopy_same_source_raw_cannot_accumulate",true);
}
catch(InvalidOperationException){Check("recopy_same_source_raw_cannot_accumulate",true);}
Check("duplicate_attempt_leaves_saved_23_6",Same(workspace.ActiveOffsets[target.Key],new(23,6)));

var fresh=context with{RunId="COPY_NEW_MACHINING",RevisionId="COPY_NEW_BASE"};
var nextSource=Point(fresh,1,2,-1,"NEW_SOURCE_RAW");var nextTarget=Point(fresh,2,0,0,"NEW_TARGET_RAW");
var next=Load(fresh,[nextSource,nextTarget],new(){{nextSource.Key,new(13,-1)},{nextTarget.Key,new(23,6)}});
var nextCopy=next.PreviewMeasurementReferenceCopy(nextSource.Key,[nextTarget.Key]);next.ConfirmMeasurementReferenceCopy(nextCopy,[nextTarget.Key],"fresh run residual");
next.BuildMeasurementReplacementCandidate([nextTarget.Key]);Commit(next,"NEW_COPY_SAVED",true);
Check("new_raw_can_add_new_residual_to_current_target",Same(next.ActiveOffsets[nextTarget.Key],new(25,5)));

var result=new{passed=failures==0,count=checks.Count,failed=failures,
 assumptions=new[]{"current B equals offset used for each synthetic machining","Scale100 percent","unit linear plant","synthetic field evaluator, no actual CSV or equipment"},tests=checks};
var report=args.SkipWhile(a=>a!="--report").Skip(1).FirstOrDefault()??"incremental-loop.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);File.WriteAllText(report,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"Incremental loop: {checks.Count-failures}/{checks.Count} PASS");return failures==0?0:1;
