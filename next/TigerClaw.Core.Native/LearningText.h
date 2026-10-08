// Adapted from lvyww/tigirl (local ime checkout), GPL-3.0.
// Source identities and C# parity coverage are recorded in README.md.
#pragma once
#include "ConfigParser.h"
#include "LexiconFile.h"
#include <stdexcept>
namespace tiger::core {
inline std::string learningUtf8(std::u16string_view text) {
    auto bytes = EncodeUtf8Text(text, false);
    return std::string(bytes.begin(), bytes.end());
}
inline std::u16string learningUtf16(std::string_view input) {
    std::vector<std::uint8_t> bytes{static_cast<std::uint8_t>('x')};
    bytes.insert(bytes.end(), input.begin(), input.end());
    auto text = DecodeLexiconBytes(bytes).substr(1);
    if (learningUtf8(text) != input) throw std::invalid_argument("Invalid learning UTF-8");
    return text;
}
// UTF-8 TSV fields: ordinary Chinese remains literal; only separators, slashes
// and control/surrogate code units need escaping. No checksum to recompute.
inline std::string editableField(std::u16string_view text, bool userAdjustment=false) {
    std::u16string escaped;
    const char16_t* digits=u"0123456789abcdef";
    for(std::size_t i=0;i<text.size();++i) {
        auto c=text[i];
        if(userAdjustment && c==u' ')escaped+=u"\\s";
        else if(userAdjustment && c==u'\r' && i+1<text.size() && text[i+1]==u'\n'){escaped+=u"\\n";++i;}
        else if(userAdjustment && c==u'\n')escaped+=u"\\u000a";
        else if(c==u'\\')escaped+=u"\\\\";
        else if(c==u'\t')escaped+=u"\\t";
        else if(c==u'\n')escaped+=u"\\n";
        else if(c==u'\r')escaped+=u"\\r";
        else if(c>=0xd800 && c<=0xdbff && i+1<text.size() && text[i+1]>=0xdc00 && text[i+1]<=0xdfff){escaped+=c;escaped+=text[++i];}
        else if(c<32 || (c>=0xd800 && c<=0xdfff)) {
            escaped+=u"\\u";for(int shift=12;shift>=0;shift-=4)escaped+=digits[(c>>shift)&15];
        }else escaped+=c;
    }
    return learningUtf8(escaped);
}
inline std::u16string editableValue(std::string_view field, bool userAdjustment=false) {
    auto text=learningUtf16(field);std::u16string result;
    for(std::size_t i=0;i<text.size();++i) {
        auto c=text[i];if(c!=u'\\'){result+=c;continue;}
        if(++i==text.size())throw std::runtime_error("Incomplete text escape");
        c=text[i];
        if(c==u'\\')result+=u'\\';else if(c==u't')result+=u'\t';
        else if(c==u's' && userAdjustment)result+=u' ';
        else if(c==u'n'){if(userAdjustment)result+=u'\r';result+=u'\n';}else if(c==u'r')result+=u'\r';
        else if(c==u'u') {
            unsigned value=0;
            for(int j=0;j<4;++j) {
                if(++i==text.size())throw std::runtime_error("Incomplete Unicode escape");
                c=text[i];unsigned d=c>=u'0'&&c<=u'9'?c-u'0':c>=u'a'&&c<=u'f'?c-u'a'+10:c>=u'A'&&c<=u'F'?c-u'A'+10:16;
                if(d==16)throw std::runtime_error("Invalid Unicode escape");
                value=(value<<4)|d;
            }result+=static_cast<char16_t>(value);
        }else throw std::runtime_error("Unknown text escape");
    }
    return result;
}
}
