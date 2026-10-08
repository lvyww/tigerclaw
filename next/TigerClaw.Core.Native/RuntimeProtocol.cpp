#include "RuntimeProtocol.h"
#include "LearningText.h"
#include "RuntimePaths.h"
#include "TextElements.h"
#include <nlohmann/json.hpp>
#include <cmath>
#include <charconv>
#ifdef _WIN32
#include <windows.h>
#endif

namespace tiger::core
{
    namespace
    {
        using Json = nlohmann::json;
        std::string FieldName(std::string text)
        { for(auto& c:text) if(c>='A' && c<='Z') c=static_cast<char>(c+32); return text; }
        bool Has(const Json& value,const char* name) { return value.contains(FieldName(name)); }
        std::uint64_t TickMilliseconds()
        {
#ifdef _WIN32
            return GetTickCount64();
#else
            return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
#endif
        }
        std::string Text(const Json& value, const char* name, const char* fallback = "")
        {
            auto i=value.find(FieldName(name)); if(i==value.end() || i->is_null()) return fallback;
            if(i->is_string()) return i->get<std::string>();
            if(i->is_boolean()) return i->get<bool>() ? "True" : "False";
            if(i->is_number_float())
            {
                char buffer[64]; auto result=std::to_chars(buffer,buffer+64,i->get<double>());
                if(result.ec==std::errc{}) return {buffer,result.ptr};
            }
            return i->dump();
        }
        std::u16string Wide(const Json& value, const char* name) { return learningUtf16(Text(value, name)); }
        int Number(const Json& value, const char* name, int fallback = 0)
        {
            auto i = value.find(FieldName(name));
            if (i == value.end() || i->is_null()) return fallback;
            if(i->is_number_unsigned() && i->get<std::uint64_t>()>INT64_MAX) return fallback;
            if(i->is_number_integer()) return static_cast<std::int32_t>(i->get<std::int64_t>());
            std::int32_t parsed=0;
            return ParseIntegerToken(TrimText(learningUtf16(Text(value,name))),parsed) ? parsed : fallback;
        }
        bool Flag(const Json& value, const char* name, bool fallback = false)
        {
            auto text=Text(value,name); auto folded=FoldOrdinalCode(learningUtf16(text));
            if(folded==u"TRUE" || text=="1") return true;
            if(folded==u"FALSE" || text=="0") return false;
            return fallback;
        }
        Json Response(int seq, bool success, bool handled)
        { return {{"type","response"}, {"seq",seq}, {"success",success}, {"handled",handled}}; }
        std::u16string Config(const RuntimeLexiconSnapshot& snapshot, std::u16string_view key)
        { for (const auto& [name,value] : snapshot.config) if (name == key) return value; return {}; }
        bool ConfigFlag(const RuntimeLexiconSnapshot& snapshot, std::u16string_view key, bool fallback = true)
        { return ParseConfigBool(Config(snapshot,key),fallback); }
        void HookConfig(Json& response, const RuntimeLexiconSnapshot& snapshot)
        {
            response["native_hook_alt_backslash_toggle_enabled"] = ConfigFlag(snapshot,u"Alt+\\启用或禁用外挂版");
            response["auto_switch_system_layout_enabled"] = ConfigFlag(snapshot,u"自动切换系统语言");
            response["use_clipboard_commit"] = ConfigFlag(snapshot,u"使用剪贴板上屏",false);
            response["clipboard_commit_whitelist"] = learningUtf8(TrimText(Config(snapshot,u"使用剪贴板上屏白名单")));
        }
        bool Hook(const Json& message)
        { return FoldOrdinalCode(Wide(message,"frontend")) == u"HOOK_NATIVE"; }
        int ConfigNumber(const RuntimeLexiconSnapshot& snapshot, std::u16string_view key, int fallback, int limit = 60000)
        {
            std::int32_t value = 0;
            return ParseIntegerToken(TrimText(Config(snapshot,key)),value) ? std::clamp(value,0,limit) : fallback;
        }
        std::u16string DisplayCode(const RuntimeInputSnapshot& state, const RuntimeLexiconSnapshot& config)
        {
            auto mask = Config(config,u"编码伪装"); auto starts = TextElementStarts(mask);
            if (starts.empty()) return state.displayCode;
            auto active = state.mode == RuntimeInputMode::Sentence ? state.displayCode : state.activeCode;
            std::u16string text = state.mode == RuntimeInputMode::Sentence ? u"" : state.displayCode.substr(0,state.displayCode.size()-active.size());
            for (auto c : active)
            {
                if (IsDotNetWhiteSpace(c)) { text += c; continue; }
                if (c >= u'A' && c <= u'Z') c += u'a'-u'A';
                auto pos = std::u16string_view(u"abcdefghijklmnopqrstuvwxyz;").find(c);
                auto index = (pos == std::u16string_view::npos ? 0 : pos) % starts.size();
                auto end = index+1 < starts.size() ? starts[index+1] : mask.size();
                text += mask.substr(starts[index],end-starts[index]);
            }
            return text;
        }
        std::vector<std::u16string> Lines(std::u16string_view text)
        {
            std::vector<std::u16string> lines;
            while (!text.empty())
            {
                auto end = text.find(u'\n');
                lines.emplace_back(TrimText(text.substr(0,end)));
                if (end == text.npos) break;
                text.remove_prefix(end+1);
            }
            return lines;
        }
        void State(Json& response, const RuntimeInputSnapshot& state, const RuntimeLexiconSnapshot& config)
        {
            response["keyboard_open"] = state.isChinese;
            response["input_buffer"] = learningUtf8(DisplayCode(state,config));
            response["input_cursor"] = -1; // no full-pinyin engine in this repository
            response["composition_tracking"] = state.mode == RuntimeInputMode::Sentence && state.composing;
            response["composition_pending"] = state.decodePending;
        }
    }
    std::optional<std::string> RuntimeProtocol::Handle(std::string_view bytes, CtrlSpaceState::Time now)
    {
        std::lock_guard lock(_mutex);
        int seq = 0;
        try
        {
            if (bytes.size() > 262144) throw std::length_error("Core request exceeds 256 KiB");
            auto parsedMessage = nlohmann::ordered_json::parse(bytes);
            if (!parsedMessage.is_object()) throw std::invalid_argument("Core request must be an object");
            Json message=Json::object();
            for(auto it=parsedMessage.begin();it!=parsedMessage.end();++it) message[FieldName(it.key())]=it.value();
            seq = Number(message, "seq");
            auto type = Text(message, "type");
            if(type!="key" && type!="caret" && type!="query_state" && type!="get_schema_list") _backgroundUntil=0;
            if (type == "key")
            {
                auto identity = KeyRequestReplayCache::BuildKey(Wide(message,"client_session"), Wide(message,"event_id"));
                return _replay.Execute(identity, seq, [&]
                {
                    if (!Text(message, "candidate_token").empty()) throw std::invalid_argument("Full-pinyin candidate tokens are unsupported");
                    InputKeyEvent event;
                    event.vk = Number(message,"vk"); event.scan = Number(message, Has(message,"scan") && !message["scan"].is_null() ? "scan" : "scan_code");
                    event.repeat = Number(message,"repeat",1); event.action = Wide(message,"action");
                    event.shift = Flag(message,"shift"); event.ctrl = Flag(message,"ctrl"); event.alt = Flag(message,"alt"); event.win = Flag(message,"win");
                    event.extended = Flag(message,"extended");
                    event.capsLock = Flag(message,Has(message,"capsLock") && !message["capslock"].is_null() ? "capsLock" : "caps_lock");
                    event.numLock = Flag(message,Has(message,"numLock") && !message["numlock"].is_null() ? "numLock" : "num_lock");
                    auto ack = Number(message,"learning_ack_version");
                    auto client = Wide(message,"client_session");
                    auto before = _input.CaptureSnapshot(); auto wasChinese = before.isChinese;
                    auto action = FoldOrdinalCode(event.action);
                    bool down = action == u"DOWN" || action == u"KEY_DOWN";
                    bool caret = message.contains("caret_x") && !message["caret_x"].is_null() && message.contains("caret_y") && !message["caret_y"].is_null();
                    if (Hook(message)) _hookNative = true;
                    if (down)
                    {
                        _backgroundUntil=0;
                        _holdBlocked = false;
                        if (caret) UpdateCaret(Number(message,"caret_x"),Number(message,"caret_y"),Number(message,"width",_caretWidth),Number(message,"height",_caretHeight));
                        if (ConfigFlag(*_runtime.Read(),u"开启打字音效(娱乐)",false))
                        { ++_soundSeq; _soundVk=event.vk; _soundVolume=ConfigNumber(*_runtime.Read(),u"按键音量0~100",30,100); }
                    }
                    OutputContext output;
                    output.clock=[] { return ReadLocalOutputClock(); };
                    output.randomIndex=[this](std::size_t count) { return _random.Choose(count); };
                    auto result = _input.Process(event, now,std::move(output));
                    if (result.action==OutputAction::OpenAddWord && _command)
                    {
                        // The key has already been applied. A missing/crashed UI
                        // must not discard its replay record or reapply the key.
                        try { _command(RuntimeUiCommand::ShowAddCi); } catch (...) {}
                    }
                    if (result.action==OutputAction::ToggleHideCandidates)
                    {
                        bool changed=false; std::u16string error;
                        _input.SetConfig(u"隐藏候选",ConfigFlag(*_runtime.Read(),u"隐藏候选",false) ? u"否" : u"是",changed,error);
                        try { _runtime.SaveConfig(_config); } catch (...) {}
                    }
                    auto response = Response(seq, true, result.handled);
                    // Always consume ready events, even for an older frontend.
                    auto receipt = _input.IssueLearningReceipt(ack == 1 ? client : std::u16string{}, result);
                    if (!receipt.empty()) response["learning_receipt"] = receipt;
                    auto after = _input.CaptureSnapshot();
                    if (before.composing && result.handled && !after.composing && after.isChinese && wasChinese==after.isChinese &&
                        !result.text.empty() && !event.ctrl && !event.alt && !event.win && event.vk!=0x1b && event.vk!=0x14 && event.vk!=0x10)
                    {
                        auto duration=ConfigNumber(*_runtime.Read(),u"上屏后候选窗驻留时间(毫秒)",0);
                        _backgroundUntil=duration>0 ? TickMilliseconds()+duration : 0;
                    }
                    State(response, after, *_runtime.Read());
                    if (after.composing && !result.text.empty()) _anchorPending=true;
                    else if (!after.composing) _anchorPending=false;
                    // Ordinary deletion to empty does not ClearCompositionInput
                    // in C#; retain its frame identity. Explicit clears/commits do.
                    bool pinyinSymbol=before.mode==RuntimeInputMode::Pinyin && !event.shift &&
                        (event.vk==0xbb || event.vk==0xbc || event.vk==0xbd || event.vk==0xbe || event.vk==0xdb || event.vk==0xdc || event.vk==0xdd || event.vk==0xbf);
                    bool pinyinSelection=before.mode==RuntimeInputMode::Pinyin && !event.shift &&
                        _runtime.Read()->selectionKeys.Number(ResolveSelectionVirtualKey(event.vk,event.scan,event.extended)).has_value();
                    if (before.composing && !after.composing &&
                        (before.mode==RuntimeInputMode::Ordinary || before.mode==RuntimeInputMode::Sentence || pinyinSymbol || pinyinSelection || wasChinese!=after.isChinese || result.cancelCompositionBeforePass) &&
                        (event.vk!=8 || before.mode==RuntimeInputMode::Sentence)) _frameSession=learningId();
                    if (!before.composing && after.composing && (after.mode==RuntimeInputMode::Sentence ||
                        (after.mode==RuntimeInputMode::Ordinary && ConfigFlag(*_runtime.Read(),u"中英文不限长混合输入",false)))) _frameSession=learningId();
                    if (before.composing && before.schemaName!=after.schemaName &&
                        (before.mode==RuntimeInputMode::Ordinary || before.mode==RuntimeInputMode::Sentence)) _frameSession=learningId();
                    if (down)
                    {
                        if (!after.composing || caret) _freshCaretDeadline={};
                        else if (!before.composing) _freshCaretDeadline=std::chrono::steady_clock::now()+std::chrono::milliseconds(30);
                    }
                    if (Hook(message))
                    {
                        auto snapshot = _runtime.Read(); HookConfig(response,*snapshot);
                        if (wasChinese != _input.CaptureSnapshot().isChinese && ConfigFlag(*snapshot,u"自动切换系统语言"))
                            response["ensure_system_layout_en"] = true;
                    }
                    if (!result.text.empty()) response["commit_text"] = learningUtf8(result.text);
                    if (result.cancelCompositionBeforePass || _pendingFrontendCompositionReset) response["cancel_composition"] = true;
                    _pendingFrontendCompositionReset = false;
                    if (down) response["expect_keyup"] = _input.ExpectKeyUp(event);
                    return response.dump();
                });
            }
            if (type == "learning_commit")
            { _input.AcknowledgeLearningReceipt(Wide(message,"client_session"),Text(message,"learning_receipt"),Flag(message,"applied")); return {}; }
            if (type == "focus" || type == "composition_canceled")
            {
                _input.CancelLearningReceipts();
                if (Hook(message)) _hookNative=true;
                if (type == "focus")
                {
                    std::int64_t hwnd=0;
                    try { auto raw=learningUtf8(TrimText(learningUtf16(Text(message,"hwnd")))); std::size_t end=0; auto n=std::stoll(raw,&end); if(end==raw.size()) hwnd=n; } catch(...) {}
                    auto identity = Json::array({hwnd,Number(message,"processId"),Text(message,"processName"),Text(message,"className"),Text(message,"windowTitle")}).dump();
                    if (identity != _focusIdentity) { _focusIdentity=std::move(identity); _input.FocusChanged(); _holdBlocked=true; _frameSession=learningId(); }
                }
                else { _input.Cancel(); _holdBlocked=true; _frameSession=learningId(); }
                _anchorPending=false; _freshCaretDeadline={};
                return {};
            }
            if (type == "caret")
            {
                if (Hook(message)) _hookNative=true;
                UpdateCaret(Number(message,"x"),Number(message,"y"),Number(message,"width",2),Number(message,"height",20));
                _freshCaretDeadline={}; return {};
            }
            if (type == "hook_native_disabled")
            {
                _hookNative=true; _hookDisabled=Flag(message,"disabled");
                if (_hookDisabled) { _input.Cancel(); _holdBlocked=true; _anchorPending=false; _freshCaretDeadline={}; _frameSession=learningId(); }
                return {};
            }
            if (type == "ime_active")
            { _hookNative=false; _imeActive=Flag(message,"active"); if (!_imeActive) { _holdBlocked=true; _anchorPending=false; _frameSession=learningId(); } return {}; }
            if ((type == "hello" || type == "query_state") && Hook(message)) _hookNative=true;
            auto response = Response(seq, true, type != "query_state" && type != "hello");
            if (type == "query_state")
            {
                auto snapshot = _runtime.Read();
                response["config_version"] = snapshot->configVersion; response["lexicon_version"] = snapshot->lexiconVersion;
            }
            else if (type == "hello")
            {
                response["protocol_version"] = 2;
                response["core_build"] = "next-dev";
                response["core_commit"] = "next"; response["core_branch"] = "next";
#ifdef _WIN32
                std::wstring executable(32768,L'\0');
                auto length=GetModuleFileNameW(nullptr,executable.data(),static_cast<DWORD>(executable.size()));
                executable.resize(length);
                response["core_path"]=learningUtf8(std::filesystem::path(executable).u16string());
#else
                response["core_path"]="";
#endif
                response["ensure_system_layout_en"] = ConfigFlag(*_runtime.Read(),u"自动切换系统语言");
                HookConfig(response,*_runtime.Read());
            }
            else if (type == "get_schema_list")
            {
                auto snapshot = _runtime.Read(); std::u16string schemas;
                for (const auto& name : GetSchemaNames(snapshot->root)) { if (!schemas.empty()) schemas += u'\n'; schemas += name; }
                response["schema_list"] = learningUtf8(schemas); response["current_schema"] = learningUtf8(Config(*snapshot,u"当前码表"));
            }
            else if (type == "get_config")
            {
                auto snapshot = _runtime.Read(); auto data = SerializeConfig(snapshot->config);
                std::string text(data.begin(), data.end());
                std::erase(text,'\r');
                if (!text.empty() && text.back() == '\n') text.pop_back();
                response["config_text"] = text; response["config_version"] = snapshot->configVersion;
            }
            else if (type == "construct_ci")
                response["code"] = learningUtf8(ConstructWordCode(Wide(message,"text"), _runtime.Read()->schema->construction));
            else if (type == "open_mb_folder")
            {
                auto snapshot=_runtime.Read(); auto path=snapshot->root/snapshot->schemaName;
                bool exists=!snapshot->schemaName.empty() && std::filesystem::exists(path);
                response["success"]=response["handled"]=exists;
                if(exists) response["path"]=learningUtf8(path.u16string());
            }
            else if (type == "open_official")
                response["success"]=response["handled"]=_openTarget && _openTarget(u"https://github.com/lvyww/tigerclaw",false);
            else if (type == "export_mb")
            {
                auto path=_runtime.ExportLexicon();
                bool opened=_openTarget && (_openTarget(path.u16string(),true) || _openTarget(path.parent_path().u16string(),false));
                response["success"]=response["handled"]=opened;
            }
            else if (type == "show_config" || type == "show_addci" || type == "show_menu" || type == "exit_core")
            {
                if (_command) _command(type=="show_config" ? RuntimeUiCommand::ShowConfig : type=="show_addci" ? RuntimeUiCommand::ShowAddCi : type=="show_menu" ? RuntimeUiCommand::ShowMenu : RuntimeUiCommand::ExitCore);
            }
            else if (type == "get_last_ci")
                response["text"] = learningUtf8(_input.Session().PostProcessor().History().LastWord(std::max(0, Number(message,"history_len"))));
            else if (type == "get_send_history_count")
                response["count"] = _input.Session().PostProcessor().History().VisibleCount();
            else if (type == "full_pinyin_info") response["full_pinyin"] = false;
            else if (type == "delete_pinyin_word") response["success"]=response["handled"]=false;
            else if (type == "pinyin_preferences") response["items"] = "";
            else if (type == "pinyin_manage")
            { response["success"]=response["handled"]=false; response["error"]=learningUtf8(u"当前不是拼音方案"); }
            else if (type == "get_selection_key_config" || type == "set_selection_key_config")
            {
                if (type == "set_selection_key_config")
                {
                    auto lines = Lines(Wide(message,"config_text"));
                    auto parsed = _runtime.SaveSelectionBindings(lines);
                    response["success"] = response["handled"] = parsed.success;
                    if (!parsed.success) response["error"] = learningUtf8(parsed.error);
                }
                response["config_text"] = learningUtf8(BuildSelectionBindingsText(_runtime.Read()->selectionBindings));
                if (type == "get_selection_key_config")
                {
                    response["default_text"] = learningUtf8(BuildSelectionBindingsText(DefaultSelectionBindings()));
                    response["config_path"] = learningUtf8((_config.parent_path() / u"自定义选重键.txt").u16string());
                }
            }
            else if (type == "add_ci")
            {
                auto code = Wide(message,"code"), text = Wide(message,"text");
                bool ok = _runtime.AdjustCandidate(CandidateAdjustment::Add,code,text);
                response["success"] = response["handled"] = ok;
                if (!ok) response["error"] = NormalizeCode(code).empty() ? "code is empty" : "text is empty";
            }
            else if (type == "ctrl_space")
            {
                bool enabled = ConfigFlag(*_runtime.Read(),u"Ctrl+空格切换中英文");
                response["handled"] = enabled;
                if (enabled)
                {
                    bool composing=_input.CaptureSnapshot().composing;
                    auto text = _input.ToggleLanguage();
                    if (composing) _frameSession=learningId();
                    if (!text.empty()) response["commit_text"] = learningUtf8(text);
                    response["input_buffer"] = "";
                }
            }
            else if (type == "set_config")
            {
                auto key = Wide(message,"key"), value = Wide(message,"value");
                auto beforeConfig=_input.CaptureSnapshot();
                bool changed = false; std::u16string error;
                bool ok = _input.SetConfig(key,value,changed,error);
                response["success"] = response["handled"] = ok;
                if (!error.empty()) response["error"] = learningUtf8(error);
                if (ok && changed)
                {
                    auto afterConfig=_input.CaptureSnapshot();
                    if (beforeConfig.composing && beforeConfig.schemaName!=afterConfig.schemaName &&
                        (beforeConfig.mode==RuntimeInputMode::Ordinary || beforeConfig.mode==RuntimeInputMode::Sentence)) _frameSession=learningId();
                    auto k = FoldOrdinalCode(TrimText(key));
                    if (k == u"中英文不限长混合输入" || k == u"自动启用整句模式" ||
                        k == u"高频字仅使用最优码组句" || k == u"整句允许全码组句白名单" || k == u"允许单字重码组句" || k == u"全拼纠正学习")
                    { _pendingFrontendCompositionReset |= _input.CaptureSnapshot().composing; _input.Cancel(); _frameSession=learningId(); }
                    try { _runtime.SaveConfig(_config); } catch (...) {} // C# best-effort persistence
                }
                auto snapshot = _runtime.Read();
                response["changed"] = changed;
                response["config_version"] = snapshot->configVersion; response["lexicon_version"] = snapshot->lexiconVersion;
            }
            else if (type == "reload_config" || type == "reload_mb")
            {
                bool composed = _input.CaptureSnapshot().composing;
                if (type == "reload_config")
                {
                    _input.Cancel();
                    _frameSession=learningId();
                    _pendingFrontendCompositionReset |= composed;
                }
                try
                {
                    if (type == "reload_config") _input.Reload(_config);
                    else _input.ReloadTables();
                }
                catch (...) { response["success"]=false; response["handled"]=type=="reload_config"; }
                if(type=="reload_config") _input.ApplyDefaultLanguage();
                auto snapshot = _runtime.Read();
                if (type == "reload_config") response["config_version"] = snapshot->configVersion;
                response["lexicon_version"] = snapshot->lexiconVersion;
            }
            else response = Response(seq, false, false); // no false success for unported commands
            auto state = _input.CaptureSnapshot();
            if (type == "query_state")
            { State(response, state, *_runtime.Read()); if (Hook(message)) HookConfig(response,*_runtime.Read()); }
            else if (type!="hello") response["keyboard_open"] = state.isChinese;
            return response.dump();
        }
        catch (const std::exception& error)
        {
            auto response = Response(seq, false, false); response["error"] = error.what();
            return response.dump();
        }
    }

    std::string RuntimeProtocol::CaptureUiState()
    {
        std::lock_guard lock(_mutex);
        auto state = _input.CaptureSnapshot(); auto config = _runtime.Read();
        auto text = [&](std::u16string_view key) { return learningUtf8(Config(*config,key)); };
        auto flag = [&](std::u16string_view key, bool fallback = true) { return ConfigFlag(*config,key,fallback); };
        auto number = [&](std::u16string_view key,int fallback=0) { return ConfigNumber(*config,key,fallback); };
        auto collect = [](std::u16string_view word,const TextMap& map)
        {
            std::u16string result; auto starts=TextElementStarts(word);
            for (std::size_t i=0; i<starts.size(); ++i)
            {
                auto end=i+1<starts.size() ? starts[i+1] : word.size();
                auto found=map.find(std::u16string(word.substr(starts[i],end-starts[i])));
                if (found==map.end() || found->second.empty()) return std::u16string{};
                if (!result.empty()) result+=u'·';
                result+=found->second;
            }
            return result;
        };
        Json candidates=Json::array(),annotations=Json::array();
        auto& metadata=config->schema->metadata;
        for (const auto& candidate: state.page.entries)
        {
            candidates.push_back(learningUtf8(_input.CandidateDisplay(candidate)));
            std::u16string annotation;
            if (CandidateCommitText(candidate)==candidate)
            {
                auto found=metadata.comments.find(candidate);
                auto comment=found==metadata.comments.end() ? std::u16string{} : found->second;
                if (state.mode==RuntimeInputMode::Pinyin)
                {
                    for (auto part:{collect(candidate,metadata.splits),collect(candidate,metadata.fullCodes),comment})
                        if (!part.empty()) { if (!annotation.empty()) annotation+=u" | "; annotation+=part; }
                }
                else
                {
                    if (flag(u"显示拆分",false)) annotation=collect(candidate,metadata.splits);
                    if (flag(u"显示注释") && !comment.empty())
                    { if (!annotation.empty()) annotation+=u' '; annotation+=comment; }
                }
            }
            annotations.push_back(learningUtf8(annotation));
        }
        int composition=1;
        switch (state.mode)
        {
        case RuntimeInputMode::English: composition=0; break;
        case RuntimeInputMode::Ordinary: composition=2; break;
        case RuntimeInputMode::UpperCase: composition=3; candidates=Json::array(); annotations=Json::array(); break;
        case RuntimeInputMode::Pinyin: composition=4; break;
        case RuntimeInputMode::Sentence: composition=5; break;
        default: break;
        }
        bool fresh=std::chrono::steady_clock::now() < _freshCaretDeadline;
        bool pendingEmpty=state.page.entries.empty() && state.decodePending;
        bool visible=state.composing && !fresh && !pendingEmpty;
        bool hold=state.composing && !fresh && pendingEmpty && state.isChinese && !_hookDisabled && (_hookNative || _imeActive) && !_holdBlocked;
        double font=17;
        try { std::size_t consumed=0; auto raw=text(u"字体大小"); auto parsed=std::stod(raw,&consumed); if (consumed==raw.size() && std::isfinite(parsed)) font=std::clamp(parsed,3.0,200.0); } catch (...) {}
        Json ui={
            {"IsOff",_hookDisabled},{"IsChinese",state.isChinese},{"StatusText",_hookDisabled ? learningUtf8(u"禁") : state.isChinese ? learningUtf8(u"中") : "EN"},
            {"CandidateVisible",visible},{"InputCode",learningUtf8(DisplayCode(state,*config))},{"Candidates",candidates},
            {"CompositionState",composition},{"CaretX",_caretX},{"CaretY",_caretY},{"CaretHeight",_caretHeight},
            {"VerticalCandidates",flag(u"竖排候选")},{"ShowCandidateIndex",flag(u"显示候选序号")},
            {"HideCandidateItems",flag(u"隐藏候选",false)},{"CodeMasking",text(u"编码伪装")},
            {"ThemeName",text(u"主题")},{"FontName",text(u"字体")},{"FontSize",font},{"CandidateAnnotations",annotations},
            {"HideStatusBar",flag(u"隐藏状态栏",false) || (!_hookNative && !_imeActive)},
            {"SoundSeq",_soundSeq},{"SoundVk",_soundVk},{"SoundVolumePercent",_soundVolume},
            {"ShowInputCodeInCandidateWindow",flag(u"候选窗显示编码",false)},
            {"CandidateExpandDelayMs",number(u"延时显示候选(毫秒)")},
            {"AnnotationExpandDelayMs",composition==4 ? 0 : number(u"延时展开注释和拆分(毫秒)")},
            {"IsNativeHook",_hookNative},{"SelectedCandidateIndex",composition==5 && state.selectedCandidateIndex<state.page.entries.size() ? static_cast<int>(state.selectedCandidateIndex) : -1},
            {"CandidateAnchorRevision",_anchorRevision},{"CandidateBackgroundUntil",_backgroundUntil},
            {"CandidateAnimationEnabled",flag(u"候选窗动效")},{"CandidateAnimationDurationMs",number(u"候选窗动效时间(毫秒)",200)},
            {"CandidateResidenceDurationMs",number(u"上屏后候选窗驻留时间(毫秒)")},
            {"CandidateHoldWhilePending",hold},{"CandidateFrameSession",_frameSession},{"CandidateSelectionToken",nullptr}
        };
        return ui.dump();
    }
}
