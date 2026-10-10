const installed = new WeakSet();

/** Preserves the page's audio-clock suspension across visibility, freeze and page-cache transitions. */
export function installEngineAudioLifecycle(engine) {
    if (installed.has(engine)) return;
    installed.add(engine);
    let pageHidden = false;
    let frozen = false;
    const refresh = () => engine.SetAudioPageActive(!document.hidden && !pageHidden && !frozen);
    document.addEventListener('visibilitychange', refresh);
    document.addEventListener('freeze', () => { frozen = true; refresh(); });
    document.addEventListener('resume', () => { frozen = false; refresh(); });
    window.addEventListener('pagehide', () => { pageHidden = true; refresh(); });
    window.addEventListener('pageshow', () => { pageHidden = false; refresh(); });
    refresh();
}
