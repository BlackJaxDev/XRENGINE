import { dotnet } from './_framework/dotnet.js';

const status = document.querySelector('#status');

try {
    const runtime = await dotnet.create();
    const exitCode = await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
    if (typeof exitCode === 'number' && exitCode !== 0) {
        throw new Error(`The managed spike exited with code ${exitCode}.`);
    }
    status.textContent = 'Jolt browser spike passed. See the console for the final box position and ray hit count.';
    status.dataset.state = 'passed';
} catch (error) {
    status.textContent = `Jolt browser spike failed: ${error?.message ?? String(error)}`;
    status.dataset.state = 'failed';
    console.error(error);
}
