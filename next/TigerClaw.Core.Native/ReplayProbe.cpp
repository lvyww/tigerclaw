#include "ReplayProbe.h"
#include "KeyRequestReplayCache.h"
#include <nlohmann/json.hpp>
#include <iostream>
#include <memory>

int RunReplayProbe()
{
    using tiger::core::KeyRequestReplayCache;
    auto cache = std::make_unique<KeyRequestReplayCache>();
    int executions = 0;
    std::string line;
    while (std::getline(std::cin, line))
    {
        if (line.size() > 262144) throw std::runtime_error("replay probe line too long");
        auto input = nlohmann::json::parse(line);
        auto units = [&](const char* field)
        {
            std::u16string value;
            if (!input.contains(field) || input.at(field).is_null()) return value;
            for (const auto& unit : input.at(field))
            {
                int code = unit.get<int>();
                if (code < 0 || code > 65535) throw std::runtime_error("invalid UTF-16 unit");
                value.push_back(static_cast<char16_t>(code));
            }
            return value;
        };
        auto key = KeyRequestReplayCache::BuildKey(units("session"), units("event"));
        std::string op = input.at("op");
        nlohmann::json output;
        if (op == "reset")
        {
            cache = std::make_unique<KeyRequestReplayCache>(input.value("capacity", 512));
            executions = 0;
            output = {{"ok", true}};
        }
        else if (op == "build")
            output["key"] = key.empty() ? nlohmann::json(nullptr) : nlohmann::json(std::vector<std::uint16_t>(key.begin(), key.end()));
        else if (op == "store")
        {
            cache->Store(key, input.value("response", std::string{}));
            output = {{"ok", true}};
        }
        else if (op == "get")
        {
            auto response = cache->Get(key, input.value("seq", 0));
            output = {{"hit", response.has_value()}, {"response", response ? nlohmann::json(*response) : nlohmann::json(nullptr)}};
        }
        else if (op == "execute")
        {
            auto response = cache->Execute(key, input.value("seq", 0), [&]
            { ++executions; return input.value("response", std::string{}); });
            output = {{"response", response}, {"executions", executions}};
        }
        else throw std::runtime_error("unknown replay probe operation");
        std::cout << output.dump() << std::endl;
    }
    return 0;
}
