"""Headless Linux librime/librime-lua; all data and test-only fault hooks isolated."""
import argparse
from pathlib import Path
import subprocess
from run_regressions import isolated_sources, temporary_tree

FAULT_ADAPTER = r'''
local function find_slot(root, name)
    local seen = {}
    local function visit(fn)
        if seen[fn] then return end
        seen[fn] = true
        local children = {}
        for i = 1, 200 do
            local n, v = debug.getupvalue(fn, i)
            if not n then break end
            if n == name then return fn, i end
            if type(v) == "function" then children[#children + 1] = v end
        end
        for _, child in ipairs(children) do
            local f, i = visit(child)
            if f then return f, i end
        end
    end
    return visit(root)
end
local tiger = require("tiger_sentence")
local processor = tiger.processor
local armed = false
tiger_sentence_processor = function(key, env)
    local context = env.engine.context
    if context:get_option("review_model_failure") then
        context:set_option("review_model_failure", false)
        tiger.set_model_enabled(false)
        tiger.set_model_enabled(true)
        local f, i = find_slot(tiger.decode, "kn_model")
        assert(f, "Missing model test seam")
        debug.setupvalue(f, i, {logp = function() error("injected page read failure") end})
        armed = true
    end
    local result = processor(key, env)
    if armed then
        local status = tiger.model_status()
        if status.error and status.error:find("runtime n%-gram failure") then
            context:set_property("review_model_failed", "yes")
            armed = false
        end
    end
    return result
end
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True)
    parser.add_argument("--shared-data", help="Optional Rime shared data; defaults to a minimal isolated preset")
    parser.add_argument("--plugin", required=True)
    args = parser.parse_args()
    with temporary_tree() as root:
        isolated_sources(root)
        # Keep the real schema/processor chain; only the table is a tiny fixture.
        table = "刘\tvp\n甲\tab\n乙\tab\n一\tcd\n"
        table += "".join(f"{chr(0x4e00+i)}\tja\n" for i in range(22))
        (root / "tiger_sentence.codes.txt").write_text(table, encoding="utf-8")
        (root / "tiger_sentence.custom.yaml").write_text("patch:\n  tiger_sentence/high_freq_limit: 0\n", encoding="utf-8")
        with (root / "rime.lua").open("a", encoding="utf-8") as file:
            file.write(FAULT_ADAPTER)
        shared = Path(args.shared_data).resolve() if args.shared_data else root / "_shared"
        if not args.shared_data:
            shared.mkdir()
            (shared / "default.yaml").write_text(
                'config_version: "1.0"\nschema_list:\n  - schema: tiger_sentence\n'
                'menu:\n  page_size: 5\nrecognizer:\n  patterns: {}\n', encoding="utf-8")
        subprocess.run([str(Path(args.exe).resolve()), str(root), str(shared),
                        str(Path(args.plugin).resolve())], check=True, timeout=90)


if __name__ == "__main__":
    main()
