#include "llama.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <exception>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
    struct Request
    {
        std::string id;
        std::vector<std::string> candidates;
    };

    std::vector<std::string> SplitTabs(const std::string& line)
    {
        std::vector<std::string> values;
        std::size_t start = 0;
        while (true)
        {
            const std::size_t separator = line.find('\t', start);
            values.push_back(line.substr(start, separator - start));
            if (separator == std::string::npos)
            {
                return values;
            }
            start = separator + 1;
        }
    }

    std::vector<Request> LoadRequests(const std::string& path)
    {
        std::ifstream input(path);
        if (!input)
        {
            throw std::runtime_error("cannot open request file: " + path);
        }

        std::vector<Request> requests;
        std::string line;
        while (std::getline(input, line))
        {
            if (!line.empty() && line.back() == '\r')
            {
                line.pop_back();
            }
            if (line.empty())
            {
                continue;
            }
            std::vector<std::string> fields = SplitTabs(line);
            if (fields.size() < 2)
            {
                throw std::runtime_error("request line has no candidates");
            }
            Request request;
            request.id = std::move(fields.front());
            request.candidates.assign(
                std::make_move_iterator(fields.begin() + 1),
                std::make_move_iterator(fields.end()));
            requests.push_back(std::move(request));
        }
        return requests;
    }

    std::vector<llama_token> Tokenize(
        const llama_vocab* vocab,
        const std::string& text)
    {
        const int32_t required = -llama_tokenize(
            vocab, text.data(), static_cast<int32_t>(text.size()),
            nullptr, 0, false, true);
        if (required <= 0)
        {
            throw std::runtime_error("tokenization failed");
        }
        std::vector<llama_token> tokens(required);
        const int32_t count = llama_tokenize(
            vocab, text.data(), static_cast<int32_t>(text.size()),
            tokens.data(), static_cast<int32_t>(tokens.size()), false, true);
        if (count != required)
        {
            throw std::runtime_error("tokenization returned an unexpected size");
        }
        return tokens;
    }

    void BatchAdd(
        llama_batch& batch,
        llama_token token,
        llama_pos position,
        llama_seq_id sequence,
        bool output)
    {
        const int32_t index = batch.n_tokens++;
        batch.token[index] = token;
        batch.pos[index] = position;
        batch.n_seq_id[index] = 1;
        batch.seq_id[index][0] = sequence;
        batch.logits[index] = output ? 1 : 0;
    }

    void BatchAddShared(
        llama_batch& batch,
        llama_token token,
        llama_pos position,
        int32_t sequenceCount,
        bool output)
    {
        const int32_t index = batch.n_tokens++;
        batch.token[index] = token;
        batch.pos[index] = position;
        batch.n_seq_id[index] = sequenceCount;
        for (int32_t sequence = 0; sequence < sequenceCount; ++sequence)
        {
            batch.seq_id[index][sequence] = sequence;
        }
        batch.logits[index] = output ? 1 : 0;
    }

    double LogProbability(const float* logits, int32_t vocabularySize, llama_token target)
    {
        float maximum = logits[0];
        for (int32_t index = 1; index < vocabularySize; ++index)
        {
            maximum = std::max(maximum, logits[index]);
        }
        double sum = 0.0;
        for (int32_t index = 0; index < vocabularySize; ++index)
        {
            sum += std::exp(static_cast<double>(logits[index] - maximum));
        }
        return static_cast<double>(logits[target] - maximum) - std::log(sum);
    }

    std::vector<double> Score(
        llama_context* context,
        const llama_vocab* vocab,
        int32_t vocabularySize,
        int32_t workerCount,
        const Request& request)
    {
        const llama_token bos = llama_vocab_bos(vocab);
        const llama_token eos = llama_vocab_eos(vocab);
        if (bos == LLAMA_TOKEN_NULL || eos == LLAMA_TOKEN_NULL)
        {
            throw std::runtime_error("model does not define BOS/EOS tokens");
        }

        std::vector<std::vector<llama_token>> sequences;
        for (const std::string& text : request.candidates)
        {
            std::vector<llama_token> sequence;
            sequence.push_back(bos);
            std::vector<llama_token> textTokens = Tokenize(vocab, text);
            sequence.insert(sequence.end(), textTokens.begin(), textTokens.end());
            sequence.push_back(eos);
            sequences.push_back(std::move(sequence));
        }

        std::size_t commonPrefix = sequences.front().size() - 1;
        for (const std::vector<llama_token>& sequence : sequences)
        {
            commonPrefix = std::min(commonPrefix, sequence.size() - 1);
        }
        for (std::size_t position = 0; position < commonPrefix; ++position)
        {
            const llama_token token = sequences.front()[position];
            if (std::any_of(
                    sequences.begin() + 1,
                    sequences.end(),
                    [position, token](const std::vector<llama_token>& sequence)
                    {
                        return sequence[position] != token;
                    }))
            {
                commonPrefix = position;
                break;
            }
        }
        if (commonPrefix == 0)
        {
            throw std::runtime_error("candidate sequences do not share BOS");
        }

        int32_t inputCount = static_cast<int32_t>(commonPrefix);
        for (const std::vector<llama_token>& sequence : sequences)
        {
            inputCount += static_cast<int32_t>(sequence.size() - 1 - commonPrefix);
        }
        llama_batch batch = llama_batch_init(
            inputCount, 0, static_cast<int32_t>(sequences.size()));
        for (std::size_t position = 0; position < commonPrefix; ++position)
        {
            BatchAddShared(
                batch,
                sequences.front()[position],
                static_cast<llama_pos>(position),
                static_cast<int32_t>(sequences.size()),
                true);
        }
        for (std::size_t sequenceIndex = 0; sequenceIndex < sequences.size(); ++sequenceIndex)
        {
            const std::vector<llama_token>& sequence = sequences[sequenceIndex];
            for (std::size_t position = commonPrefix; position + 1 < sequence.size(); ++position)
            {
                BatchAdd(
                    batch,
                    sequence[position],
                    static_cast<llama_pos>(position),
                    static_cast<llama_seq_id>(sequenceIndex),
                    true);
            }
        }

        llama_memory_clear(llama_get_memory(context), true);
        const int32_t decodeResult = llama_decode(context, batch);
        if (decodeResult != 0)
        {
            llama_batch_free(batch);
            throw std::runtime_error("llama_decode failed: " + std::to_string(decodeResult));
        }

        struct OutputTarget
        {
            int32_t outputIndex;
            std::size_t sequenceIndex;
            llama_token token;
        };
        std::vector<OutputTarget> targets;
        for (std::size_t position = 0; position < commonPrefix; ++position)
        {
            for (std::size_t sequenceIndex = 0; sequenceIndex < sequences.size(); ++sequenceIndex)
            {
                targets.push_back({
                    static_cast<int32_t>(position),
                    sequenceIndex,
                    sequences[sequenceIndex][position + 1]});
            }
        }
        int32_t outputIndex = static_cast<int32_t>(commonPrefix);
        for (std::size_t sequenceIndex = 0; sequenceIndex < sequences.size(); ++sequenceIndex)
        {
            const std::vector<llama_token>& sequence = sequences[sequenceIndex];
            for (std::size_t position = commonPrefix; position + 1 < sequence.size(); ++position)
            {
                targets.push_back({outputIndex++, sequenceIndex, sequence[position + 1]});
            }
        }

        std::vector<double> tokenScores(targets.size());
        std::atomic<std::size_t> nextTarget(0);
        const int32_t threadCount = std::max(
            1, std::min(workerCount, static_cast<int32_t>(targets.size())));
        std::vector<std::thread> workers;
        workers.reserve(threadCount);
        for (int32_t threadIndex = 0; threadIndex < threadCount; ++threadIndex)
        {
            workers.emplace_back([&]()
            {
                while (true)
                {
                    const std::size_t targetIndex = nextTarget.fetch_add(1);
                    if (targetIndex >= targets.size())
                    {
                        return;
                    }
                    const OutputTarget& target = targets[targetIndex];
                    // llama_get_logits_ith() addresses the original batch token
                    // index. Multi-sequence decoding may internally reorder those
                    // indices. llama_get_logits() is the documented contiguous
                    // output array in batch appearance order, which is what
                    // outputIndex records here.
                    const float* logits = llama_get_logits(context) +
                        static_cast<std::size_t>(target.outputIndex) * vocabularySize;
                    tokenScores[targetIndex] = LogProbability(
                        logits, vocabularySize, target.token);
                }
            });
        }
        for (std::thread& worker : workers)
        {
            worker.join();
        }

        std::vector<double> scores(sequences.size(), 0.0);
        for (std::size_t targetIndex = 0; targetIndex < targets.size(); ++targetIndex)
        {
            scores[targets[targetIndex].sequenceIndex] += tokenScores[targetIndex];
        }
        llama_batch_free(batch);
        return scores;
    }

    double ScoreCandidate(
        llama_context* context,
        const llama_vocab* vocab,
        int32_t vocabularySize,
        int32_t workerCount,
        const std::string& text)
    {
        const llama_token bos = llama_vocab_bos(vocab);
        const llama_token eos = llama_vocab_eos(vocab);
        std::vector<llama_token> sequence;
        sequence.push_back(bos);
        std::vector<llama_token> textTokens = Tokenize(vocab, text);
        sequence.insert(sequence.end(), textTokens.begin(), textTokens.end());
        sequence.push_back(eos);

        const int32_t inputCount = static_cast<int32_t>(sequence.size()) - 1;
        llama_batch batch = llama_batch_init(inputCount, 0, 1);
        for (int32_t position = 0; position < inputCount; ++position)
        {
            BatchAdd(batch, sequence[position], position, 0, true);
        }
        llama_memory_clear(llama_get_memory(context), true);
        const int32_t decodeResult = llama_decode(context, batch);
        if (decodeResult != 0)
        {
            llama_batch_free(batch);
            throw std::runtime_error("independent llama_decode failed: " +
                std::to_string(decodeResult));
        }

        std::vector<double> tokenScores(inputCount);
        const float* allLogits = llama_get_logits(context);
        auto calculate = [&](int32_t position)
        {
            tokenScores[position] = LogProbability(
                allLogits + static_cast<std::size_t>(position) * vocabularySize,
                vocabularySize,
                sequence[position + 1]);
        };
        if (workerCount <= 1)
        {
            for (int32_t position = 0; position < inputCount; ++position)
            {
                calculate(position);
            }
        }
        else
        {
            std::atomic<int32_t> nextPosition(0);
            std::vector<std::thread> workers;
            workers.reserve(workerCount);
            for (int32_t threadIndex = 0; threadIndex < workerCount; ++threadIndex)
            {
                workers.emplace_back([&]()
                {
                    while (true)
                    {
                        const int32_t position = nextPosition.fetch_add(1);
                        if (position >= inputCount)
                        {
                            return;
                        }
                        calculate(position);
                    }
                });
            }
            for (std::thread& worker : workers)
            {
                worker.join();
            }
        }

        double score = 0.0;
        for (double tokenScore : tokenScores)
        {
            score += tokenScore;
        }
        llama_batch_free(batch);
        return score;
    }

    std::vector<double> ScoreCandidatesIndependently(
        const std::vector<llama_context*>& contexts,
        const llama_vocab* vocab,
        int32_t vocabularySize,
        int32_t workersPerCandidate,
        const Request& request)
    {
        if (request.candidates.size() > contexts.size())
        {
            throw std::runtime_error("not enough persistent candidate contexts");
        }
        std::vector<double> scores(request.candidates.size());
        std::vector<std::exception_ptr> errors(request.candidates.size());
        std::vector<std::thread> candidateThreads;
        candidateThreads.reserve(request.candidates.size());
        for (std::size_t candidateIndex = 0;
             candidateIndex < request.candidates.size();
             ++candidateIndex)
        {
            candidateThreads.emplace_back([&, candidateIndex]()
            {
                try
                {
                    scores[candidateIndex] = ScoreCandidate(
                        contexts[candidateIndex],
                        vocab,
                        vocabularySize,
                        workersPerCandidate,
                        request.candidates[candidateIndex]);
                }
                catch (...)
                {
                    errors[candidateIndex] = std::current_exception();
                }
            });
        }
        for (std::thread& candidateThread : candidateThreads)
        {
            candidateThread.join();
        }
        for (const std::exception_ptr& error : errors)
        {
            if (error)
            {
                std::rethrow_exception(error);
            }
        }
        return scores;
    }
}

int main(int argc, char** argv)
{
    if (argc < 3 || argc > 6)
    {
        std::cerr << "usage: sentence-qwen-gguf-scorer MODEL REQUESTS [THREADS]"
                     " [NO_REPACK] [PARALLEL_CANDIDATES]\n";
        return 2;
    }

    try
    {
        const int32_t threads = argc >= 4 ? std::stoi(argv[3]) :
            static_cast<int32_t>(std::thread::hardware_concurrency());
        bool useRepack = true;
        bool parallelCandidates = false;
        for (int index = 4; index < argc; ++index)
        {
            const std::string option = argv[index];
            if (option == "NO_REPACK")
            {
                useRepack = false;
            }
            else if (option == "PARALLEL_CANDIDATES")
            {
                parallelCandidates = true;
            }
            else
            {
                throw std::runtime_error("unknown option: " + option);
            }
        }
        std::vector<Request> requests = LoadRequests(argv[2]);
        if (requests.empty())
        {
            throw std::runtime_error("request file is empty");
        }

        const auto loadStarted = std::chrono::steady_clock::now();
        llama_backend_init();
        llama_model_params modelParams = llama_model_default_params();
        modelParams.n_gpu_layers = 0;
        modelParams.use_extra_bufts = useRepack;
        llama_model* model = llama_model_load_from_file(argv[1], modelParams);
        if (model == nullptr)
        {
            throw std::runtime_error("cannot load model");
        }
        const auto loadEnded = std::chrono::steady_clock::now();

        const llama_vocab* vocab = llama_model_get_vocab(model);
        const int32_t vocabularySize = llama_vocab_n_tokens(vocab);
        std::vector<llama_context*> contexts;
        const int32_t contextCount = parallelCandidates ? 5 : 1;
        for (int32_t contextIndex = 0; contextIndex < contextCount; ++contextIndex)
        {
            llama_context_params contextParams = llama_context_default_params();
            // Independent candidates use smaller contexts, but keep five of
            // them alive so per-key timings never include context creation.
            contextParams.n_ctx = parallelCandidates ? 64 : 256;
            contextParams.n_batch = parallelCandidates ? 32 : 128;
            contextParams.n_ubatch = parallelCandidates ? 32 : 128;
            contextParams.n_seq_max = parallelCandidates ? 1 : 5;
            contextParams.n_outputs_max = parallelCandidates ? 32 : 128;
            contextParams.n_outputs_max_per_seq = 32;
            contextParams.n_threads = threads;
            contextParams.n_threads_batch = threads;
            contextParams.no_perf = true;
            contextParams.kv_unified = true;
            llama_context* context = llama_init_from_model(model, contextParams);
            if (context == nullptr)
            {
                for (llama_context* createdContext : contexts)
                {
                    llama_free(createdContext);
                }
                llama_model_free(model);
                throw std::runtime_error("cannot create context");
            }
            contexts.push_back(context);
        }

        std::cout << std::fixed << std::setprecision(6);
        std::cout << "LOAD\t"
                  << std::chrono::duration<double, std::milli>(loadEnded - loadStarted).count()
                  << '\n';
        for (const Request& request : requests)
        {
            const auto started = std::chrono::steady_clock::now();
            const std::vector<double> scores = parallelCandidates
                ? ScoreCandidatesIndependently(
                    contexts, vocab, vocabularySize, threads, request)
                : Score(
                    contexts.front(), vocab, vocabularySize, threads, request);
            const auto ended = std::chrono::steady_clock::now();
            std::cout << "RESULT\t" << request.id << '\t'
                      << std::chrono::duration<double, std::milli>(ended - started).count();
            for (double score : scores)
            {
                std::cout << '\t' << score;
            }
            std::cout << '\n';
        }

        for (llama_context* context : contexts)
        {
            llama_free(context);
        }
        llama_model_free(model);
        llama_backend_free();
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "error: " << error.what() << '\n';
        return 1;
    }
}
