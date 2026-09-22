using System.Runtime.InteropServices;
namespace TigerClaw.Pinyin;
internal sealed class Kenlm : IPinyinLanguageModel, IIndexedPinyinLanguageModel, IDisposable
{
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr joint_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern void joint_free(IntPtr model);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr joint_error();
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern uint joint_index(IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string token);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern double joint_score(IntPtr model, uint a, uint b, uint c);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr joint_contexts_create(IntPtr model, uint[] pairs, uint count);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern void joint_contexts_free(IntPtr contexts);
    [DllImport("jointkenlm", CallingConvention=CallingConvention.Cdecl)] static extern int joint_contexts_score(IntPtr contexts,uint token,[Out] double[] scores);
    internal sealed class ContextBatch : IDisposable
    {
        private readonly Kenlm lease;
        private IntPtr context;
        private readonly int count;
        private readonly int[] positions;
        private readonly double[] uniqueScores;
        internal ContextBatch(Kenlm owner,(uint A,uint B)[] pairs)
        {
            lease=owner.CreateQuery();count=pairs.Length;
            var distinct=new Dictionary<(uint,uint),int>();positions=new int[count];
            for(int i=0;i<count;i++){if(!distinct.TryGetValue(pairs[i],out int position)){position=distinct.Count;distinct.Add(pairs[i],position);}positions[i]=position;}
            uniqueScores=new double[distinct.Count];var values=new uint[distinct.Count*2];
            foreach(var pair in distinct){values[2*pair.Value]=pair.Key.Item1;values[2*pair.Value+1]=pair.Key.Item2;}
            try{context=joint_contexts_create(lease.handle,values,(uint)distinct.Count);if(context==IntPtr.Zero)throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));}
            catch{lease.Dispose();throw;}
        }
        internal void Score(uint token,double[] output)
        {
            ObjectDisposedException.ThrowIf(context==IntPtr.Zero,this);
            if(output.Length<count)throw new ArgumentException("Score output is shorter than the context batch",nameof(output));
            if(joint_contexts_score(context,token,uniqueScores)==0)throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));
            for(int i=0;i<uniqueScores.Length;i++)uniqueScores[i]=uniqueScores[i]*Math.Log(10)+2.0;
            for(int i=0;i<count;i++)output[i]=uniqueScores[positions[i]];
        }
        public void Dispose(){if(context==IntPtr.Zero)return;joint_contexts_free(context);context=IntPtr.Zero;lease.Dispose();GC.SuppressFinalize(this);}
        ~ContextBatch(){Dispose();}
    }
    internal ContextBatch CreateContexts((uint A,uint B)[] pairs)=>new(this,pairs);
    private static int batchUnavailable;
    internal ContextBatch? TryCreateContexts((uint A,uint B)[] pairs)
    {
        if(Volatile.Read(ref batchUnavailable)!=0)return null;
        try{return CreateContexts(pairs);}
        catch(EntryPointNotFoundException){Volatile.Write(ref batchUnavailable,1);return null;}
    }
    private sealed class Mapping : SafeHandle
    {
        internal Mapping(IntPtr value):base(IntPtr.Zero,true){SetHandle(value);}
        public override bool IsInvalid=>handle==IntPtr.Zero;
        protected override bool ReleaseHandle(){joint_free(handle);return true;}
    }
    private readonly Mapping mapping;
    private readonly bool query;
    private bool disposed;
    internal bool ProfileEnabled;
    internal long ProfileCalls,ProfileMisses,ProfileNativeTicks;
    internal void ResetProfile(){ProfileCalls=ProfileMisses=ProfileNativeTicks=0;}
    private IntPtr handle => mapping.DangerousGetHandle();
    private readonly Dictionary<string,uint> ids = new(StringComparer.Ordinal);
    // Four-way set associative cache: bounded memory, no full-table clear cliff.
    private struct CachedScore { internal uint A, B, C; internal double Value; internal bool Valid; }
    private CachedScore[]? scores;
    private byte[]? nextVictim;
    internal Kenlm(string path)
    {
        var value=joint_load(path);
        if(value==IntPtr.Zero)throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));
        mapping=new Mapping(value);
    }
    private Kenlm(Mapping mapping)
    {
        this.mapping=mapping;
        bool acquired=false;mapping.DangerousAddRef(ref acquired);query=acquired;
    }
    internal Kenlm CreateQuery(){ObjectDisposedException.ThrowIf(disposed,this);return new(mapping);}
    public uint Index(string text)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!ids.TryGetValue(text,out var id))ids[text]=id=joint_index(handle,text=="\u0002"?"<s>":text=="\u0003"?"</s>":text);
        return id;
    }
    public double LogProbability(string a,string b,string c) => LogProbability(Index(a),Index(b),Index(c));
    public double LogProbability(uint a, uint b, uint c)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(ProfileEnabled)ProfileCalls++;
        scores ??= new CachedScore[262144]; nextVictim ??= new byte[65536];
        uint hash = unchecked((a * 2654435761u) ^ (b * 2246822519u) ^ (c * 3266489917u));
        int bucket = (int)((hash ^ (hash >> 16)) & 65535), start = bucket * 4;
        for (int i = start; i < start + 4; i++)
        {
            ref var item = ref scores[i];
            if (item.Valid && item.A == a && item.B == b && item.C == c) return item.Value;
        }
        bool sample=ProfileEnabled&&((++ProfileMisses&127)==0);
        long sampleStart=sample?System.Diagnostics.Stopwatch.GetTimestamp():0;
        double result = joint_score(handle,a,b,c)*Math.Log(10);
        if(sample)ProfileNativeTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-sampleStart;
        if(!double.IsFinite(result))throw new InvalidDataException(Marshal.PtrToStringUTF8(joint_error()));
        int slot = start + (nextVictim[bucket]++ & 3);
        scores[slot] = new CachedScore { A=a, B=b, C=c, Value=result, Valid=true };
        return result;
    }
    public bool HasObservedBigram(string a,string b)=>throw new NotSupportedException("Not used by full-pinyin Beam");
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(query)mapping.DangerousRelease();else mapping?.Dispose();
        GC.SuppressFinalize(this);
    }
    ~Kenlm(){Dispose();}
}
