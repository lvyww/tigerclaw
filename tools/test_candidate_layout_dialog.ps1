param([Parameter(Mandatory=$true)][string]$DialogPath)
$ErrorActionPreference='Stop'
$env:TIGERCLAW_TEST_PIPE='TigerClaw.Core.Native.Test.Layout.'+[Guid]::NewGuid().ToString('N')
Add-Type -AssemblyName PresentationFramework
Add-Type -ReferencedAssemblies System.Web.Extensions @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Web.Script.Serialization;
public class LayoutPipeFixture : IDisposable {
 public ConcurrentQueue<string> Requests=new ConcurrentQueue<string>();
 public volatile bool FailSet;
 public volatile bool Stop;
 public string Mode="4";
 private readonly string name;
 private Task worker;
 public LayoutPipeFixture(string n) { name=n;worker=Task.Run((Action)Run); }
 void Run() {
  while(!Stop) using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Message)) {
   pipe.WaitForConnection(); if(Stop)return;
   using(var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true))
   using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true)) {
    string raw=reader.ReadLine();if(raw==null)continue;Requests.Enqueue(raw);
    var json=new JavaScriptSerializer();var req=json.Deserialize<Dictionary<string,object>>(raw);
    string kind=(string)req["type"];
    var response=new Dictionary<string,object>{{"success",true},{"changed",true},{"seq",1},{"full_pinyin",false}};
    if(kind=="set_config") {
     if(FailSet) {response["success"]=false;response["error"]="injected failure";}
     else if((string)req["key"]=="候选布局")Mode=(string)req["value"];
    }
    if(kind=="get_schema_list") {response["schema_names"]=new[]{"Plain","Other"};response["current_schema"]="Plain";}
    if(kind=="get_config")response["config_text"]="候选布局\t"+Mode+"\n当前码表\tOther\n";
    writer.WriteLine(json.Serialize(response));writer.Flush();
   }
  }
 }
 public void Dispose(){Stop=true;using(var p=new NamedPipeClientStream(".",name,PipeDirection.InOut)){try{p.Connect(1000);}catch{}}worker.Wait(2000);}
}
'@
$server=[LayoutPipeFixture]::new($env:TIGERCLAW_TEST_PIPE)
try {
 $assembly=[Reflection.Assembly]::LoadFrom($DialogPath)
 $type=$assembly.GetType('TigerClaw.Dialog.ConfigWindow')
 $flags=[Reflection.BindingFlags]'Instance,NonPublic'
 $window=[Activator]::CreateInstance($type)
 $type.GetField('_suppressChangeTracking',$flags).SetValue($window,$true)
 $config=New-Object 'System.Collections.Generic.Dictionary[string,string]'
 $config.Add('候选布局','4')
 $config.Add('手动加词快捷键','Ctrl+VK_OEM_PLUS')
 $config.Add('切换最近码表快捷键','Ctrl+VK_M')
 $type.GetMethod('SetupDynamicEditors',$flags).Invoke($window,@($config.PSObject.BaseObject,$false))|Out-Null
 $entries=$type.GetField('_dynamicEntries',$flags).GetValue($window)
 $entry=@($entries | Where-Object Key -eq '候选布局')[0]
 $combo=$entry.Editor
 if($combo -isnot [Windows.Controls.ComboBox] -or $combo.Items.Count -ne 5){throw 'Expected five-choice ComboBox'}
 if(($combo.Items -join '|') -ne '横排带编码|竖排带编码|横排无编码|竖排无编码|仅编码'){throw 'Wrong labels'}
 $original=$type.GetField('_original',$flags).GetValue($window)
 $collect=$type.GetMethod('CollectCurrentValues',$flags)
 $save=$type.GetMethod('TrySaveChanges',$flags)
 $dirty=$type.GetMethod('HasUnsavedChanges',$flags)
 # Establish the actual editor baseline, including other built-in controls.
 foreach($kv in $collect.Invoke($window,@()).GetEnumerator()){$original[$kv.Key]=$kv.Value}
 $type.GetField('_suppressChangeTracking',$flags).SetValue($window,$false)
 if($dirty.Invoke($window,@($false))){throw 'Fresh dialog marked dirty'}
 foreach($mode in 1..5) {
  $before=$server.Requests.Count
  $combo.SelectedIndex=$mode-1
  $values=$collect.Invoke($window,@())
  if($values['候选布局'] -ne [string]$mode){throw 'Wrong serialization'}
  foreach($old in @('竖排候选','候选窗显示编码','隐藏候选')){if($values.ContainsKey($old)){throw 'Old setting leaked'}}
  $argsSave=[object[]]@($false,$false,$false)
  if(-not $save.Invoke($window,$argsSave)){throw ('Save failed: '+$type.GetField('StatusText',$flags).GetValue($window).Text+' requests='+($server.Requests.ToArray() -join ';'))}
  $requests=@($server.Requests.ToArray() | Select-Object -Skip $before | ForEach-Object {$_|ConvertFrom-Json})
  $sets=@($requests | Where-Object type -eq 'set_config')
  if($sets.Count -ne 1 -or $sets[0].key -ne '候选布局' -or $sets[0].value -ne [string]$mode){throw 'Layout must use exactly one config write'}
  if($dirty.Invoke($window,@($false))){throw 'Saved layout still dirty'}
 }
 $server.FailSet=$true;$combo.SelectedIndex=0
 $argsSave=[object[]]@($false,$false,$false)
 if($save.Invoke($window,$argsSave)){throw 'Injected failure was ignored'}
 if(-not $dirty.Invoke($window,@($false))){throw 'Failed save lost dirty state'}
 if($original['候选布局'] -ne '5'){throw 'Failed save changed baseline'}
 $server.FailSet=$false
 # Reload/scheme reconstruction must show the server's current mode.
 $type.GetMethod('LoadConfig',$flags).Invoke($window,@($false))|Out-Null
 $entries=$type.GetField('_dynamicEntries',$flags).GetValue($window)
 $combo=@($entries | Where-Object Key -eq '候选布局')[0].Editor
 if($combo.SelectedIndex -ne 4){throw 'Reload lost saved selection'}
 $search=$type.GetField('SearchBox',$flags).GetValue($window)
 $search.Text='仅编码';$type.GetMethod('ApplySearchFilter',$flags).Invoke($window,@())|Out-Null
 $filters=$type.GetField('_filterEntries',$flags).GetValue($window)
 $row=@($filters | Where-Object Key -eq '候选布局')[0]
 if($row.Container.Visibility -ne [Windows.Visibility]::Visible){throw 'Layout not searchable'}
 Write-Output 'PASS real WPF five-choice editor, single IPC writes, save failure, reload and search; isolated pipe only'
} finally {$server.Dispose()}
