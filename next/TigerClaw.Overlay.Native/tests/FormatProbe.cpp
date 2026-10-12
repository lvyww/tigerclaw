#include "Model.h"
#include <nlohmann/json.hpp>
#include <iostream>
#include <algorithm>
using namespace tiger::overlay;
int main()
{
    try
    {
        std::string line;
        while (std::getline(std::cin, line))
        {
            auto request = nlohmann::json::parse(line);
            State state;
            if (!ParseState(request.at("state").dump(), state)) throw std::runtime_error("Invalid state");
            auto display = Format(state, request.at("expanded").get<bool>(), request.at("annotations").get<bool>());
            display = OrientDisplay(state, display, request.value("above", false));
            const char* mode = "Hidden";
            switch (display.mode)
            {
            case DisplayMode::CodeOnly: mode = "CodeOnly"; break;
            case DisplayMode::InputOnly: mode = "InputOnly"; break;
            case DisplayMode::CodeAndCandidates: mode = "CodeAndCandidates"; break;
            case DisplayMode::CandidatesOnly: mode = "CandidatesOnly"; break;
            default: break;
            }
            auto spans = display.annotationRanges;
            spans.erase(std::remove_if(spans.begin(), spans.end(), [](const auto& span) { return span.first < 0; }), spans.end());
            std::sort(spans.begin(), spans.end());
            std::cout << nlohmann::json{{"mode", mode}, {"text", ToUtf8(display.text)},
                {"start", display.selectionStart}, {"length", display.selectionLength},
                {"annotations", spans}, {"codeStart", display.codeStart}, {"codeLength", display.codeLength}}.dump() << '\n';
        }
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
