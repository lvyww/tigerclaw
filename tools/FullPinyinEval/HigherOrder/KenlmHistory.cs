using System.Runtime.InteropServices;
namespace TigerClaw.Pinyin;
// Isolated evaluation adapter: Previous2 packs older history, Previous1 the newest token.
// The full packed history participates in Beam merge/group keys and cache keys.
internal sealed class Kenlm : IPinyinLanguageModel, IDisposable
{
 [DllImport("jointkenlm")] static extern IntPtr joint_load([MarshalAs(UnmanagedType.LPUTF8Str)]string path);
 [DllImport("jointkenlm")] static extern void joint_free(IntPtr p);
 [DllImport("jointkenlm")] static extern IntPtr joint_error();
 [DllImport("jointkenlm")] static extern uint joint_index(IntPtr p,[MarshalAs(UnmanagedType.LPUTF8Str)]string t);
 [DllImport("jointkenlm")] static extern int joint_order(IntPtr p);
 [DllImport("jointkenlm")] static extern double joint_history_score(IntPtr p,uint[] h,int n,uint target);
 private IntPtr handle;internal int Order{get;}
 readonly Dictionary<string,uint> ids=new();
 readonly Dictionary<(string,string,string),double> scores=new();
 readonly Dictionary<(string,string),string> advances=new();
 readonly Dictionary<(string,string),uint[]> histories=new();
 internal Kenlm(string path){handle=joint_load(path);if(handle==IntPtr.Zero)throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));Order=joint_order(handle);}
 internal uint Index(string t){if(!ids.TryGetValue(t,out var id))ids[t]=id=joint_index(handle,t=="\u0002"?"<s>":t=="\u0003"?"</s>":t);return id;}
 internal string Advance(string a,string b){
  if(Order==3||b=="\u0002")return b;
  if(advances.TryGetValue((a,b),out var result))return result;
  result=string.Join('\u001f',a.Split('\u001f').Append(b).TakeLast(Order-2));
  if(advances.Count>=262144)advances.Clear();advances[(a,b)]=result;return result;
 }
 public double LogProbability(string a,string b,string c){
  if(scores.TryGetValue((a,b,c),out var v))return v;
  if(!histories.TryGetValue((a,b),out var h)){
   h=(b=="\u0002"?new[]{b}:a.Split('\u001f').Append(b)).TakeLast(Order-1).Reverse().Select(Index).ToArray();
   if(histories.Count>=262144)histories.Clear();histories[(a,b)]=h;
  }
  v=joint_history_score(handle,h,h.Length,Index(c))*Math.Log(10);
  if(!double.IsFinite(v))throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));
  if(scores.Count>=262144)scores.Clear();scores[(a,b,c)]=v;return v;
 }
 public bool HasObservedBigram(string a,string b)=>throw new NotSupportedException();
 public void Dispose(){if(handle!=IntPtr.Zero){joint_free(handle);handle=IntPtr.Zero;}}
}
