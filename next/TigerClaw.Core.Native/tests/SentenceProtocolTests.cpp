#include "SentenceProtocol.h"
#include <iostream>

using namespace tiger::core;
namespace { void Check(bool value) { if (!value) throw std::runtime_error("Sentence protocol regression"); } }
int main()
{
    try
    {
        SentenceNeuralRequest request{{18, 2, u"Aa2", {}}, {u"\u7532", u"\U00020000\"\n"}};
        Check(EncodeSentenceControl(SentenceControlCommand::Hello, 1).find("hello") != std::string::npos);
        Check(EncodeSentenceControl(SentenceControlCommand::Shutdown, 2).find("shutdown") != std::string::npos);
        Check(DecodeSentenceControl(R"({"type":"response","seq":1,"success":true})", 1));
        Check(!DecodeSentenceControl(R"({"type":"response","seq":1,"success":false})", 1));
        Check(!DecodeSentenceControl(R"({"type":"response","seq":1,"success":true})", 2));
        auto encoded = EncodeSentenceRerank(request, 7);
        Check(encoded.back() == '\n' && std::count(encoded.begin(), encoded.end(), '\n') == 1);
        Check(encoded.find("\"raw_code\":\"Aa2\"") != std::string::npos);
        std::string good = R"({"type":"response","seq":7,"generation":18,"raw_code":"Aa2","success":true,"scores":[-8.25,-13.7]})";
        auto scores = DecodeSentenceRerank(good, request, 7);
        Check(scores && *scores == std::vector<double>({-8.25, -13.7}));
        Check(!DecodeSentenceRerank(good, request, 8));
        auto wrong = request; wrong.composition.generation++;
        Check(!DecodeSentenceRerank(good, wrong, 7));
        wrong = request; wrong.composition.raw = u"aa2";
        Check(!DecodeSentenceRerank(good, wrong, 7));
        for (auto replacement : {"[]", "[1]", "[1,2,3]", "[null,1]", "[\"1\",2]", "[1e999,2]"})
        {
            auto invalid = good; auto start = invalid.find("[-8.25,-13.7]"); invalid.replace(start, 13, replacement);
            Check(!DecodeSentenceRerank(invalid, request, 7));
        }
        Check(!DecodeSentenceRerank("{}", request, 7));
        Check(!DecodeSentenceRerank(good + good, request, 7));
        Check(!DecodeSentenceRerank(std::string(65537, ' '), request, 7));
        bool rejected = false;
        try { EncodeSentenceRerank(request, 0); } catch (const std::invalid_argument&) { rejected = true; }
        Check(rejected);
        std::cout << "Sentence UTF-8 framing/identity/score validation tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
