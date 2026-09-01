"use strict";

(() => {
    const host = window.chrome.webview;
    const md = window.markdownit({ html: false, linkify: true, breaks: false }).use(window.brokerMathPlugin);
    md.validateLink = url => /^https?:\/\//i.test(url);
    // Responses cannot fetch tracking images, even when they use ordinary Markdown.
    md.renderer.rules.image = (tokens, index) => `[Image: ${md.utils.escapeHtml(tokens[index].content)}]`;
    const response = document.getElementById("response");
    const main = document.querySelector("main");
    let previous = null;
    let queued = null;
    let scheduled = false;
    let followTail = false;
    let scrollFrame = 0;
    let motion = false;
    const nearBottom = () => window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 40;
    const hasSelection = () => !window.getSelection().isCollapsed;
    const stopScroll = () => { cancelAnimationFrame(scrollFrame); scrollFrame = 0; };

    window.addEventListener("wheel", event => {
        stopScroll();
        if (event.deltaY < 0) followTail = false;
    }, { passive: true });
    window.addEventListener("keydown", event => {
        if (["ArrowUp", "PageUp", "Home", "ArrowDown", "PageDown", "End", " "].includes(event.key)) {
            stopScroll();
            if (["ArrowUp", "PageUp", "Home"].includes(event.key)) followTail = false;
        }
    });
    window.addEventListener("scroll", () => {
        if (!scrollFrame) followTail = nearBottom();
    }, { passive: true });
    document.addEventListener("pointerdown", () => { stopScroll(); followTail = false; });
    document.addEventListener("pointerup", () => { followTail = nearBottom() && !hasSelection(); });
    document.addEventListener("click", event => {
        const link = event.target.closest("a");
        if (!link) return;
        event.preventDefault();
        const url = link.getAttribute("href");
        if (event.isTrusted && /^https?:\/\//i.test(url || "")) host.postMessage({ type: "link", url });
    });

    function scrollToTail(immediate) {
        stopScroll();
        if (!followTail || hasSelection()) return;
        if (immediate || !motion) {
            window.scrollTo(0, document.documentElement.scrollHeight);
            return;
        }
        const step = () => {
            if (!followTail || hasSelection()) { scrollFrame = 0; return; }
            const target = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
            const distance = target - window.scrollY;
            if (Math.abs(distance) < 1) { window.scrollTo(0, target); scrollFrame = 0; return; }
            window.scrollTo(0, window.scrollY + distance * .3);
            scrollFrame = requestAnimationFrame(step);
        };
        scrollFrame = requestAnimationFrame(step);
    }

    function replaceResponse(html, animate) {
        const template = document.createElement("template");
        // Only our HTML-disabled Markdown renderer and trust:false KaTeX output reach this sink.
        template.innerHTML = html;
        const fresh = [...template.content.childNodes];
        const old = [...response.childNodes];
        let prefix = 0;
        while (prefix < old.length && prefix < fresh.length && old[prefix].isEqualNode(fresh[prefix])) prefix++;
        let suffix = 0;
        while (suffix < old.length - prefix && suffix < fresh.length - prefix &&
            old[old.length - 1 - suffix].isEqualNode(fresh[fresh.length - 1 - suffix])) suffix++;
        // Keep unchanged blocks (and their selections) alive across streaming snapshots.
        for (let index = prefix; index < old.length - suffix; index++) old[index].remove();
        const anchor = suffix ? old[old.length - suffix] : null;
        for (let index = prefix; index < fresh.length - suffix; index++) {
            const node = fresh[index];
            response.insertBefore(node, anchor);
            if (animate && index >= old.length && node.nodeType === Node.ELEMENT_NODE)
                node.animate([{ opacity: .35 }, { opacity: 1 }], { duration: 180 });
        }
    }

    function plain(id, text) {
        const element = document.getElementById(id);
        if (element.textContent !== text) element.textContent = text;
    }

    function saveSelection() {
        const selection = window.getSelection();
        if (selection.isCollapsed || !main.contains(selection.anchorNode) || !main.contains(selection.focusNode)) return null;
        const offset = (node, position) => {
            const range = document.createRange();
            range.selectNodeContents(main);
            range.setEnd(node, position);
            return range.toString().length;
        };
        return {
            anchor: [selection.anchorNode, selection.anchorOffset], focus: [selection.focusNode, selection.focusOffset],
            offsets: [offset(selection.anchorNode, selection.anchorOffset), offset(selection.focusNode, selection.focusOffset)],
            text: main.textContent,
        };
    }

    function restoreSelection(saved) {
        if (!saved) return;
        const updated = main.textContent;
        let prefix = 0;
        while (prefix < saved.text.length && prefix < updated.length && saved.text[prefix] === updated[prefix]) prefix++;
        let suffix = 0;
        while (suffix < saved.text.length - prefix && suffix < updated.length - prefix &&
            saved.text[saved.text.length - suffix - 1] === updated[updated.length - suffix - 1]) suffix++;
        const remap = offset => offset <= prefix ? offset
            : offset >= saved.text.length - suffix ? offset + updated.length - saved.text.length
            : Math.min(offset, updated.length - suffix);
        const locate = offset => {
            const walker = document.createTreeWalker(main, NodeFilter.SHOW_TEXT);
            let node;
            while ((node = walker.nextNode())) {
                if (offset <= node.length) return [node, offset];
                offset -= node.length;
            }
            return [main, main.childNodes.length];
        };
        const endpoint = (original, offset) => main.contains(original[0]) ? original : locate(remap(offset));
        window.getSelection().setBaseAndExtent(...endpoint(saved.anchor, saved.offsets[0]), ...endpoint(saved.focus, saved.offsets[1]));
    }

    function render(snapshot) {
        const sameRun = previous && snapshot.runId === previous.runId;
        const scrollY = window.scrollY;
        const selection = sameRun ? saveSelection() : null;
        const selected = sameRun && hasSelection();
        // Use an unchanged visible block to compensate for reflow above a scrolled-up reader.
        const anchor = sameRun && !followTail
            ? [...response.children].find(node => node.getBoundingClientRect().bottom > 0) : null;
        const anchorTop = anchor?.getBoundingClientRect().top;
        if (!sameRun) { stopScroll(); followTail = snapshot.active; window.getSelection().removeAllRanges(); }
        motion = snapshot.motion && !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        document.documentElement.classList.toggle("dark", snapshot.dark);
        main.hidden = !snapshot.runId;
        plain("system", snapshot.system);
        plain("prompt", snapshot.prompt);
        plain("failure", snapshot.failure);
        document.getElementById("system-section").hidden = !snapshot.system.trim();
        document.getElementById("failure-section").hidden = !snapshot.failure;
        if (!sameRun || snapshot.response !== previous.response || snapshot.active !== previous.active) {
            const html = snapshot.response ? md.render(snapshot.response)
                : `<p>${snapshot.active ? "Waiting for output…" : "No response text was returned."}</p>`;
            replaceResponse(html, sameRun && snapshot.active && motion);
        }
        previous = snapshot;
        restoreSelection(selection);
        if (followTail && !selected) scrollToTail(!sameRun);
        else if (!sameRun) window.scrollTo(0, 0);
        else window.scrollTo(0, anchor?.isConnected ? scrollY + anchor.getBoundingClientRect().top - anchorTop : scrollY);
    }

    host.addEventListener("message", event => {
        queued = event.data;
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(() => {
            scheduled = false;
            try { render(queued); }
            catch { host.postMessage({ type: "error" }); }
        });
    });
    // Font loads and viewport resizes can change the tail without a new response delta.
    new ResizeObserver(() => { if (followTail) scrollToTail(false); }).observe(main);
    window.brokerPreviewReady = true;
    host.postMessage({ type: "ready" });
})();
