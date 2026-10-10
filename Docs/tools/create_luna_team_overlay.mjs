#!/usr/bin/env node
// Host-only setup utility, never imported into Unity. Does not alter original packages.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { resolve } from 'node:path';

const [installRoot, profileRoot] = process.argv.slice(2);
if (!installRoot || !profileRoot) throw new Error('Usage: node create_luna_team_overlay.mjs <dsh install root> <profile root>');
const modules = resolve(installRoot, 'node_modules/@deepseek-ai');
const destination = resolve(profileRoot, 'overlays/katamari-luna-team');
function replaceOnce(source, before, after) {
  if (source.split(before).length !== 2) throw new Error('Upstream code changed; refusing patch: ' + before.slice(0, 60));
  return source.replace(before, after);
}
function pinImports(source, original) {
  if (!process.execArgv.includes('--experimental-import-meta-resolve')) {
    throw new Error('Use node --experimental-import-meta-resolve to preserve ESM module identity.');
  }
  return source.replace(/from "([^"\n:]+)"/g,
    (_match, specifier) => 'from ' + JSON.stringify(import.meta.resolve(specifier, pathToFileURL(original).href)));
}
const runtimeOriginal = resolve(modules, 'dsh-experimental-agent-team/lib/index.js');
const toolsOriginal = resolve(modules, 'dsh-experimental-tool-agent-team/lib/index.js');
let runtime = await readFile(runtimeOriginal, 'utf8');
runtime = replaceOnce(runtime, 'prompt: request.prompt,\n\t\t\t\t\tparent: root',
  'prompt: request.prompt,\n\t\t\t\t\tparent: root,\n\t\t\t\t\tagentOptions: request.agentOptions');
runtime = pinImports(runtime, runtimeOriginal);
let tools = await readFile(toolsOriginal, 'utf8');
tools = replaceOnce(tools, 'forkProvider: z.string().default("fork")',
  'forkProvider: z.string().default("fork"),\n\tteammateProvider: z.string().default("openai-codex"),\n\tteammateModel: z.string().default("gpt-6-luna")');
tools = replaceOnce(tools, 'provider: context === "fork" ? config.forkProvider : config.freshProvider,',
  'provider: context === "fork" ? config.forkProvider : config.freshProvider,\n\t\t\t\t\tagentOptions: { provider: config.teammateProvider, model: config.teammateModel },');
tools = replaceOnce(tools, 'forkProvider: config.forkProvider ?? "fork"',
  'forkProvider: config.forkProvider ?? "fork",\n\t\tteammateProvider: config.teammateProvider ?? "openai-codex",\n\t\tteammateModel: config.teammateModel ?? "gpt-6-luna"');
tools = pinImports(tools, toolsOriginal);
await mkdir(destination, { recursive: true });
for (const [name, content] of [['runtime.mjs', runtime], ['tools.mjs', tools]]) {
  const file = resolve(destination, name);
  await writeFile(file, content, { flag: 'wx', mode: 0o600 });
  console.log('Created non-overwriting overlay: ' + file);
}
console.log('Original packages unchanged. Profile activation is a separate explicit operation.');
