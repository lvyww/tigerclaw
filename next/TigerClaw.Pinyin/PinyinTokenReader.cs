using System.Text.Json;

namespace TigerClaw.Pinyin;

internal static class PinyinTokenReader
{
    // Incremental UTF-8 parsing avoids simultaneous UTF-16 text, JSON DOM and
    // per-occurrence token strings for the multi-million-entry token table.
    internal static Dictionary<(string Text,string Code),string[]> Read(string path)
    {
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024);
        var result=new Dictionary<(string,string),string[]>();
        var pool=new Dictionary<string,string>(StringComparer.Ordinal);
        byte[] buffer=new byte[1024*1024];int remaining=0;
        var state=new JsonReaderState();bool first=true,opened=false;
        byte property=0;string? text=null,code=null;bool inTokens=false;
        Span<char> tokenChars=stackalloc char[128];
        var lookup=pool.GetAlternateLookup<ReadOnlySpan<char>>();
        var tokens=new List<string>();
        while(true)
        {
            int read=input.Read(buffer,remaining,buffer.Length-remaining);bool final=read==0;
            int length=remaining+read;
            int offset=first&&length>=3&&buffer[0]==0xef&&buffer[1]==0xbb&&buffer[2]==0xbf?3:0;
            first=false;
            var reader=new Utf8JsonReader(buffer.AsSpan(offset,length-offset),final,state);
            while(reader.Read())
            {
                if(!opened){if(reader.TokenType!=JsonTokenType.StartArray)throw new InvalidDataException("Expected token array");opened=true;}
                switch(reader.TokenType)
                {
                    case JsonTokenType.StartObject: text=code=null;tokens.Clear();break;
                    case JsonTokenType.PropertyName: property=reader.ValueTextEquals("text"u8)?(byte)1:reader.ValueTextEquals("code"u8)?(byte)2:reader.ValueTextEquals("tokens"u8)?(byte)3:(byte)0;break;
                    case JsonTokenType.StartArray: if(property==3)inTokens=true;break;
                    case JsonTokenType.EndArray: inTokens=false;break;
                    case JsonTokenType.String:
                        if(inTokens)
                        {
                            string shared;
                            if(reader.ValueSpan.Length<=tokenChars.Length)
                            {
                                int count=reader.CopyString(tokenChars);var value=tokenChars[..count];
                                if(!lookup.TryGetValue(value,out shared!)){shared=value.ToString();pool.Add(shared,shared);}
                            }
                            else {string value=reader.GetString()!;if(!pool.TryGetValue(value,out shared!))pool[value]=shared=value;}
                            tokens.Add(shared);
                        }
                        else if(property==1)text=reader.GetString();
                        else if(property==2)code=reader.GetString();
                        break;
                    case JsonTokenType.EndObject:
                        if(text==null||code==null)throw new InvalidDataException("Missing token row identity");
                        result.Add((text,code),tokens.ToArray());property=0;break;
                }
            }
            int consumed=checked((int)reader.BytesConsumed)+offset;state=reader.CurrentState;
            remaining=length-consumed;
            if(final)break;
            buffer.AsSpan(consumed,remaining).CopyTo(buffer);
            if(remaining==buffer.Length)Array.Resize(ref buffer,checked(buffer.Length*2));
        }
        if(!opened)throw new InvalidDataException("Empty token table");
        return result;
    }
}
