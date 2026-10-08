#include "SentenceFivegramModel.h"
#include <cmath>
#include <limits>
#include <optional>
#include <stdexcept>
#include <unordered_map>
#include <vector>

namespace tiger::core
{
    namespace
    {
        [[noreturn]] void Invalid() { throw std::invalid_argument("Invalid TCSKNM03 fivegram model"); }
        struct HistoryHash
        {
            std::size_t operator()(const SentenceLmHistory& h) const
            {
                std::size_t hash = static_cast<std::size_t>(h.count);
                for (auto token : h.tokens) hash = hash * 16777619u ^ token;
                return hash;
            }
        };
        struct ScoreKey
        {
            SentenceLmHistory history;
            std::uint16_t token;
            bool operator==(const ScoreKey&) const = default;
        };
        struct ScoreHash
        {
            std::size_t operator()(const ScoreKey& key) const
            { return HistoryHash{}(key.history) * 16777619u ^ key.token; }
        };
        // Strict UTF-8, matching UTF8Encoding(false, true) in the C# reader.
        std::u16string TokenText(std::span<const std::uint8_t> bytes)
        {
            std::u16string result;
            for (std::size_t i = 0; i < bytes.size();)
            {
                auto lead = bytes[i++];
                std::uint32_t cp = lead, minimum = 0;
                int extra = 0;
                if (lead >= 0xc2 && lead <= 0xdf) { cp &= 31; extra = 1; minimum = 0x80; }
                else if (lead >= 0xe0 && lead <= 0xef) { cp &= 15; extra = 2; minimum = 0x800; }
                else if (lead >= 0xf0 && lead <= 0xf4) { cp &= 7; extra = 3; minimum = 0x10000; }
                else if (lead >= 0x80) Invalid();
                if (bytes.size() - i < static_cast<std::size_t>(extra)) Invalid();
                while (extra--)
                {
                    auto b = bytes[i++];
                    if ((b & 0xc0) != 0x80) Invalid();
                    cp = (cp << 6) | (b & 63);
                }
                if (cp < minimum || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) Invalid();
                if (cp <= 0xffff) result += static_cast<char16_t>(cp);
                else { cp -= 0x10000; result += static_cast<char16_t>(0xd800 + (cp >> 10)); result += static_cast<char16_t>(0xdc00 + (cp & 1023)); }
            }
            return result;
        }
    }
    struct SentenceFivegramModel::Data
    {
        struct Quant { double minimum = 0, step = 0, backoffMinimum = 0, backoffStep = 0; };
        struct Bucket { std::uint64_t end = 0, index = 0; std::uint32_t indexCount = 0; };
        struct Block { std::uint64_t successors = 0; int count = 0; double backoff = 0; };
        ReadOnlyMapping mapping;
        std::span<const std::uint8_t> bytes;
        int version = 0, quantBytes = 0;
        std::uint32_t stride = 0;
        std::uint16_t unknown = 0, bos = 0, eos = 0;
        std::array<Quant, 6> quant;
        std::array<std::array<Bucket, 256>, 6> buckets;
        std::unordered_map<std::u16string, std::uint16_t> tokens;
        std::vector<double> unigrams;
        void Range(std::uint64_t at, std::uint64_t size) const
        { if (at > bytes.size() || size > bytes.size() - at) Invalid(); }
        std::uint64_t Read(std::uint64_t at, unsigned width) const
        {
            Range(at, width); std::uint64_t value = 0;
            for (unsigned i = 0; i < width; ++i) value |= std::uint64_t{bytes[static_cast<std::size_t>(at + i)]} << (8 * i);
            return value;
        }
        std::uint64_t Offset(std::uint64_t at) const
        { auto value = Read(at, 8); if (value > bytes.size()) Invalid(); return value; }
        int Q(std::uint64_t at) const { return static_cast<int>(Read(at, quantBytes)); }
        double Probability(int order, int value) const { return quant[order].minimum + value * quant[order].step; }
        double Backoff(int order, int value) const
        { return value == 0 ? 0 : quant[order].backoffMinimum + (value - 1) * quant[order].backoffStep; }
        int CompareKeys(std::uint64_t a, std::uint64_t b, int length) const
        {
            for (int i = 0; i < length; ++i)
            { auto x = Read(a + i * 2, 2), y = Read(b + i * 2, 2); if (x != y) return x < y ? -1 : 1; }
            return 0;
        }
        explicit Data(const std::filesystem::path& path) : mapping(path, std::uint64_t{4} << 30), bytes(mapping.Bytes())
        {
            if (bytes.size() < 256) Invalid();
            for (std::size_t i = 0; i < 8; ++i) if (bytes[i] != std::string_view("TCSKNM03")[i]) Invalid();
            version = static_cast<int>(Read(8, 4)); quantBytes = version == 2 ? 1 : 2;
            if ((version != 1 && version != 2) || Read(12, 4) != 256 || Offset(16) != bytes.size() || Read(24, 4) != 5 || Read(32, 4) != 256) Invalid();
            auto vocabulary = Read(28, 4); auto step = Read(36, 4);
            if (!vocabulary || vocabulary > 65536 || !step || step > 65536) Invalid();
            stride = static_cast<std::uint32_t>(step);
            unknown = static_cast<std::uint16_t>(Read(56, 2)); bos = static_cast<std::uint16_t>(Read(58, 2)); eos = static_cast<std::uint16_t>(Read(60, 2));
            if (unknown >= vocabulary || bos >= vocabulary || eos >= vocabulary) Invalid();
            for (int order = 1; order <= 5; ++order)
            {
                auto at = 160 + (order - 1) * 16;
                auto signedValue = [&](int p)
                { auto value = Read(p, 4); return value >= 0x80000000ull ? static_cast<double>(value) - 4294967296.0 : static_cast<double>(value); };
                double scale = version == 2 ? 1e9 : 1e12;
                quant[order] = {signedValue(at) / 1e7, Read(at + 4, 4) / scale, signedValue(at + 8) / 1e7, Read(at + 12, 4) / scale};
            }
            unigrams.resize(static_cast<std::size_t>(vocabulary));
            auto pos = Offset(40), size = Offset(48); Range(pos, size); auto end = pos + size;
            for (std::uint32_t id = 0; id < vocabulary; ++id)
            {
                auto count = Read(pos, 2); pos += 2;
                if (!count || pos > end || count + 2 * quantBytes > end - pos) Invalid();
                auto text = TokenText(bytes.subspan(static_cast<std::size_t>(pos), static_cast<std::size_t>(count))); pos += count;
                if (!tokens.emplace(std::move(text), static_cast<std::uint16_t>(id)).second) Invalid();
                unigrams[id] = Probability(1, Q(pos)); pos += 2 * quantBytes;
            }
            auto special = [&](std::u16string_view text, std::uint16_t id)
            { auto it = tokens.find(std::u16string(text)); return it != tokens.end() && it->second == id; };
            if (pos != end || !special(u"<s>", bos) || !special(u"</s>", eos) || !special(u"<unk>", unknown)) Invalid();
            for (int order = 2; order <= 5; ++order)
            {
                auto section = 64 + (order - 2) * 24; auto directory = Offset(section); Range(directory, 256 * 40);
                std::uint64_t blocks = 0, records = 0;
                for (int bucket = 0; bucket < 256; ++bucket)
                {
                    auto at = directory + bucket * 40;
                    auto start = Offset(at), length = Offset(at + 8), index = Offset(at + 16);
                    auto indices = Read(at + 24, 4), count = Read(at + 28, 4), recordCount = Read(at + 32, 8);
                    if (indices != (count + stride - 1) / stride || indices > std::numeric_limits<int>::max()) Invalid();
                    Range(start, length); Range(index, indices * 16);
                    if (index != start + length) Invalid();
                    std::uint64_t previous = 0;
                    for (std::uint64_t i = 0; i < indices; ++i)
                    {
                        auto ip = index + i * 16, offset = Offset(ip + 8);
                        if (offset < start || offset >= index || (i && offset <= previous) || (!i && offset != start)) Invalid();
                        for (int j = 0; j < order - 1; ++j)
                        {
                            auto id = Read(ip + j * 2, 2);
                            if (id >= vocabulary || (!j && id % 256 != static_cast<unsigned>(bucket)) || Read(offset + j * 2, 2) != id) Invalid();
                        }
                        if (i && CompareKeys(ip - 16, ip, order - 1) >= 0) Invalid();
                        previous = offset;
                    }
                    if (!count && (length || recordCount)) Invalid();
                    buckets[order][bucket] = {index, index, static_cast<std::uint32_t>(indices)};
                    blocks += count;
                    if (recordCount > std::numeric_limits<std::uint64_t>::max() - records) Invalid();
                    records += recordCount;
                }
                if (blocks != Read(section + 8, 8) || records != Read(section + 16, 8)) Invalid();
            }
        }
        int Compare(std::uint64_t at, const SentenceLmHistory& history, int length) const
        {
            for (int i = 0; i < length; ++i)
            { auto a = Read(at + i * 2, 2), b = std::uint64_t{history.tokens[length - i - 1]}; if (a != b) return a < b ? -1 : 1; }
            return 0;
        }
        Block FindBlock(const SentenceLmHistory& history, int length) const
        {
            auto bucket = buckets[length + 1][history.tokens[length - 1] % 256];
            std::uint32_t lo = 0, hi = bucket.indexCount;
            while (lo < hi)
            { auto mid = lo + (hi - lo) / 2; if (Compare(bucket.index + mid * 16ull, history, length) <= 0) lo = mid + 1; else hi = mid; }
            if (!lo) return {};
            auto entry = lo - 1; auto pos = Offset(bucket.index + entry * 16ull + 8);
            auto end = entry + 1 < bucket.indexCount ? Offset(bucket.index + (entry + 1) * 16ull + 8) : bucket.end;
            unsigned header = length * 2 + quantBytes + 2;
            for (std::uint32_t i = 0; pos < end && i < stride; ++i)
            {
                if (end - pos < header) Invalid();
                auto compared = Compare(pos, history, length); auto bow = Q(pos + length * 2);
                int count = static_cast<int>(Read(pos + length * 2 + quantBytes, 2));
                auto successors = pos + header, next = successors + count * static_cast<std::uint64_t>(2 + quantBytes);
                if (next > end) Invalid();
                if (!compared) return {successors, count, Backoff(length, bow)};
                if (compared > 0) return {};
                pos = next;
            }
            if (pos < end) Invalid();
            return {};
        }
        bool FindProbability(const Block& block, std::uint16_t token, int order, double& probability) const
        {
            int lo = 0, hi = block.count, width = 2 + quantBytes;
            while (lo < hi)
            { int mid = lo + (hi - lo) / 2; if (Read(block.successors + mid * width, 2) < token) lo = mid + 1; else hi = mid; }
            if (lo < block.count && Read(block.successors + lo * width, 2) == token)
            { probability = Probability(order, Q(block.successors + lo * width + 2)); return true; }
            return false;
        }
        std::optional<std::uint16_t> Token(std::u16string_view text) const
        {
            if (text == u"\x02") return bos;
            if (text == u"\x03") return eos;
            auto found = tokens.find(std::u16string(text));
            return found == tokens.end() ? std::nullopt : std::optional(found->second);
        }
    };
    struct SentenceFivegramModel::Query::Cache
    {
        std::unordered_map<SentenceLmHistory, Data::Block, HistoryHash> contexts;
        std::unordered_map<ScoreKey, double, ScoreHash> scores;
        Data::Block Context(const Data& data, SentenceLmHistory h, int length)
        {
            h.count = length;
            for (int i = length; i < 4; ++i) h.tokens[i] = 0;
            if (auto found = contexts.find(h); found != contexts.end()) return found->second;
            auto block = data.FindBlock(h, length);
            if (contexts.size() >= 8192) contexts.clear();
            contexts.emplace(h, block); return block;
        }
    };
    SentenceFivegramModel::SentenceFivegramModel(const std::filesystem::path& path) : _data(std::make_shared<const Data>(path)) {}
    int SentenceFivegramModel::Version() const { return _data->version; }
    std::unique_ptr<SentenceFivegramModel::Query> SentenceFivegramModel::CreateQuery() const { return std::make_unique<Query>(_data); }
    SentenceFivegramModel::Query::Query(std::shared_ptr<const Data> data) : _data(std::move(data)), _cache(std::make_unique<Cache>()) {}
    SentenceFivegramModel::Query::~Query() = default;
    SentenceLmHistory SentenceFivegramModel::Query::BeginHistory() const { return {{{_data->bos, 0, 0, 0}}, 1}; }
    double SentenceFivegramModel::Query::Step(SentenceLmHistory& history, std::u16string_view target)
    {
        if (history.count < 1 || history.count > 4) throw std::invalid_argument("Invalid fivegram history count");
        auto token = _data->Token(target).value_or(_data->unknown);
        ScoreKey key{history, token};
        if (auto found = _cache->scores.find(key); found != _cache->scores.end())
        { history = history.Append(token); return found->second; }
        double score = 0; bool found = false;
        for (int n = history.count; n > 0; --n)
        {
            auto block = _cache->Context(*_data, history, n); double probability = 0;
            if (_data->FindProbability(block, token, n + 1, probability)) { score += probability; found = true; break; }
            score += block.backoff;
        }
        if (!found) score += _data->unigrams[token];
        score *= std::log(10.0);
        if (_cache->scores.size() >= 8192) _cache->scores.clear();
        _cache->scores.emplace(key, score); history = history.Append(token); return score;
    }
    bool SentenceFivegramModel::Query::HasObservedBigram(std::u16string_view a, std::u16string_view b)
    {
        auto first = _data->Token(a), second = _data->Token(b); double ignored = 0;
        return first && second && _data->FindProbability(_cache->Context(*_data, {{{*first, 0, 0, 0}}, 1}, 1), *second, 2, ignored);
    }
}
