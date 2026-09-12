#include "Model.h"
#include <nlohmann/json.hpp>
#include <iostream>
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
            const char* mode = "Hidden";
            switch (display.mode)
            {
            case DisplayMode::CodeOnly: mode = "CodeOnly"; break;
            case DisplayMode::InputOnly: mode = "InputOnly"; break;
            case DisplayMode::CodeAndCandidates: mode = "CodeAndCandidates"; break;
            case DisplayMode::CandidatesOnly: mode = "CandidatesOnly"; break;
            default: break;
            }
            std::cout << nlohmann::json{{"mode", mode}, {"text", ToUtf8(display.text)},
                {"start", display.selectionStart}, {"length", display.selectionLength}}.dump() << '\n';
        }
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
