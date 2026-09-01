"use strict";

// Math is a Markdown token, not a post-processing pass over already-unescaped text.
// Markdown-it owns code fences/spans, list nesting and links; their contents stay protected.
window.brokerMathPlugin = function (md) {
    const delimiters = [["\\[", "\\]", true], ["$$", "$$", true], ["\\(", "\\)", false], ["$", "$", false]];
    const escaped = (text, index) => {
        let slashes = 0;
        while (index > 0 && text[--index] === "\\") slashes++;
        return slashes % 2 === 1;
    };
    function closing(text, from, close, singleDollar) {
        let index = from;
        while ((index = text.indexOf(close, index)) >= 0) {
            if (!escaped(text, index) && (!singleDollar ||
                (index > from && !/\s/.test(text[index - 1]) && !/[\d$]/.test(text[index + 1] || ""))))
                return index;
            index += close.length;
        }
        return -1;
    }
    function opening(text, index) {
        for (const delimiter of delimiters) {
            if (!text.startsWith(delimiter[0], index)) continue;
            // A bare monetary amount followed by prose is not an opening math delimiter.
            if (delimiter[0] === "$") {
                if (/\s/.test(text[index + 1] || " ") || text[index - 1] === "$") return null;
                if (/^\$\d[\d,.]*(?:\s+[a-zA-Z]|[;:!?]|$)/.test(text.slice(index))) {
                    const end = closing(text, index + 1, "$", true);
                    // A matched expression such as $2 x$ is math; $5 and $10 are prices.
                    if (end < 0 || /[$\n]/.test(text.slice(index + 1, end))) return null;
                }
            }
            return delimiter;
        }
        return null;
    }
    function render(token) {
        const escape = md.utils.escapeHtml;
        const tag = token.block ? "div" : "span";
        const className = token.meta.display ? "math-display" : "math-inline";
        if (token.meta.pending)
            return `<${tag} class="${className} math-source">${escape(token.meta.raw)}</${tag}>`;
        try {
            if (token.content.length > 16384) throw new Error("Formula exceeds the preview size limit.");
            const html = katex.renderToString(token.content, {
                displayMode: token.meta.display, output: "htmlAndMathml", throwOnError: true,
                trust: false, strict: "ignore", maxExpand: 1000, maxSize: 20,
                macros: {}, // Do not allow \gdef to leak between formulas or responses.
            });
            return `<${tag} class="${className}" title="${escape(token.meta.raw)}">${html}</${tag}>`;
        } catch (error) {
            return `<${tag} class="${className} math-source math-error" title="${escape(String(error))}">${escape(token.meta.raw)}</${tag}>`;
        }
    }
    md.inline.ruler.before("escape", "broker_math", (state, silent) => {
        const start = state.pos;
        const delimiter = opening(state.src, start);
        if (!delimiter) return false;
        const [open, close, display] = delimiter;
        const end = closing(state.src.slice(0, state.posMax), start + open.length, close, open === "$");
        // Keep the remaining inline source intact when a streaming token isn't closed yet.
        const next = end < 0 ? state.posMax : end + close.length;
        if (!silent) {
            const token = state.push("broker_math", "", 0);
            token.content = state.src.slice(start + open.length, end < 0 ? next : end);
            token.meta = { display, pending: end < 0, raw: state.src.slice(start, next) };
        }
        state.pos = next;
        return true;
    });
    md.block.ruler.before("fence", "broker_math_block", (state, startLine, endLine, silent) => {
        if (state.sCount[startLine] - state.blkIndent >= 4) return false;
        const start = state.bMarks[startLine] + state.tShift[startLine];
        const delimiter = opening(state.src, start);
        if (!delimiter || !delimiter[2]) return false;
        const [open, close] = delimiter;
        let lastLine = startLine;
        let end = -1;
        for (; lastLine < endLine; lastLine++) {
            if (lastLine > startLine && state.sCount[lastLine] < state.blkIndent && !state.isEmpty(lastLine)) break;
            const lineStart = lastLine === startLine ? start + open.length : state.bMarks[lastLine] + state.tShift[lastLine];
            const candidate = closing(state.src.slice(lineStart, state.eMarks[lastLine]), 0, close, false);
            if (candidate >= 0) { end = lineStart + candidate; break; }
        }
        // A same-line expression with trailing prose belongs to the inline rule.
        if (end >= 0 && state.src.slice(end + close.length, state.eMarks[lastLine]).trim()) return false;
        if (silent) return true;
        const nextLine = end < 0 ? Math.max(startLine + 1, lastLine) : lastLine + 1;
        const raw = state.getLines(startLine, nextLine, state.blkIndent, false).trim();
        const token = state.push("broker_math_block", "", 0);
        token.block = true;
        token.map = [startLine, nextLine];
        token.content = raw.slice(open.length, end < 0 ? undefined : -close.length).trim();
        token.meta = { display: true, pending: end < 0, raw };
        state.line = nextLine;
        return true;
    }, { alt: ["paragraph", "reference", "blockquote", "list"] });
    md.renderer.rules.broker_math = (tokens, index) => render(tokens[index]);
    md.renderer.rules.broker_math_block = (tokens, index) => render(tokens[index]) + "\n";
};
