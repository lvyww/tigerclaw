#include "SentenceProtocol.h"
#include "ConfigParser.h"
#include <nlohmann/json.hpp>

namespace tiger::core
{
    namespace
    {
        std::string Utf8(std::u16string_view text)
        {
            auto bytes = EncodeUtf8Text(text);
            return std::string(bytes.begin(), bytes.end());
        }
        bool ValidIdentity(const SentenceNeuralRequest& request, std::uint64_t sequence)
        {
            if (!sequence || sequence > static_cast<std::uint64_t>(INT64_MAX) ||
                request.composition.generation > static_cast<std::uint64_t>(INT64_MAX) ||
                request.candidates.empty() || request.candidates.size() > 5) return false;
            // Raw identity cannot survive replacement of an unpaired surrogate.
            auto raw = std::u16string_view(request.composition.raw);
            for (std::size_t i = 0; i < raw.size(); ++i)
            {
                auto unit = raw[i];
                if (unit >= 0xd800 && unit <= 0xdbff)
                {
                    if (++i >= raw.size() || raw[i] < 0xdc00 || raw[i] > 0xdfff) return false;
                }
                else if (unit >= 0xdc00 && unit <= 0xdfff) return false;
            }
            return true;
        }
    }
    std::string EncodeSentenceControl(SentenceControlCommand command, std::uint64_t sequence)
    {
        if (!sequence || sequence > static_cast<std::uint64_t>(INT64_MAX) ||
            (command != SentenceControlCommand::Hello && command != SentenceControlCommand::Shutdown))
            throw std::invalid_argument("Invalid sentence control request");
        return nlohmann::json{{"type", command == SentenceControlCommand::Hello ? "hello" : "shutdown"},
            {"seq", sequence}}.dump() + '\n';
    }
    bool DecodeSentenceControl(std::string_view line, std::uint64_t sequence)
    {
        if (!sequence || sequence > static_cast<std::uint64_t>(INT64_MAX) || line.empty() || line.size() > 65536) return false;
        try
        {
            auto object = nlohmann::json::parse(line);
            return object.is_object() && object.value("type", std::string{}) == "response" &&
                object.at("success").is_boolean() && object.at("success").get<bool>() &&
                object.at("seq").is_number_integer() && object.at("seq") == sequence;
        }
        catch (const nlohmann::json::exception&) { return false; }
    }
    std::string EncodeSentenceRerank(const SentenceNeuralRequest& request, std::uint64_t sequence)
    {
        if (!ValidIdentity(request, sequence)) throw std::invalid_argument("Invalid sentence rerank request");
        auto candidates = nlohmann::json::array();
        for (const auto& text : request.candidates) candidates.push_back(Utf8(text));
        nlohmann::json object{{"type", "rerank"}, {"seq", sequence},
            {"generation", request.composition.generation}, {"raw_code", Utf8(request.composition.raw)},
            {"candidates", std::move(candidates)}};
        return object.dump() + '\n';
    }
    std::optional<std::vector<double>> DecodeSentenceRerank(std::string_view line,
        const SentenceNeuralRequest& request, std::uint64_t sequence)
    {
        if (!ValidIdentity(request, sequence) || line.empty() || line.size() > 65536) return {};
        try
        {
            auto object = nlohmann::json::parse(line);
            if (!object.is_object() || object.value("type", std::string{}) != "response" ||
                !object.at("success").is_boolean() || !object.at("success").get<bool>() ||
                !object.at("seq").is_number_integer() || object.at("seq") != sequence ||
                !object.at("generation").is_number_integer() || object.at("generation") != request.composition.generation ||
                !object.at("raw_code").is_string() || object.at("raw_code").get<std::string>() != Utf8(request.composition.raw)) return {};
            const auto& scores = object.at("scores");
            if (!scores.is_array() || scores.size() != request.candidates.size()) return {};
            std::vector<double> result;
            for (const auto& item : scores)
            {
                if (!item.is_number()) return {};
                double value = item.get<double>();
                if (!std::isfinite(value)) return {};
                result.push_back(value);
            }
            return result;
        }
        catch (const nlohmann::json::exception&) { return {}; }
    }
}
