const installed = new WeakSet();

/** Connects page lifetime to the production managed client without retaining admission credentials. */
export function installEngineNetworkLifecycle(engine) {
    if (installed.has(engine)) return;
    installed.add(engine);
    let pageHidden = false;
    let frozen = false;
    const refresh = () => engine.SetNetworkPageActive(!document.hidden && !pageHidden && !frozen);
    document.addEventListener('visibilitychange', refresh);
    document.addEventListener('freeze', () => { frozen = true; refresh(); });
    document.addEventListener('resume', () => { frozen = false; refresh(); });
    window.addEventListener('pagehide', () => { pageHidden = true; refresh(); });
    window.addEventListener('pageshow', () => { pageHidden = false; refresh(); });
    refresh();
}
