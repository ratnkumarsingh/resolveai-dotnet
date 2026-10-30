// Rebuilds src/ResolveAI.App/wwwroot/app.css from Styles/app.css with Tailwind. The generated file is committed, so running
// the app needs only the .NET SDK; this is only needed after changing CSS classes. Usage:
//   npm install --prefix tools/css && node tools/css/build.mjs
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { compile, optimize } from "@tailwindcss/node";
import { Scanner } from "@tailwindcss/oxide";

const app = resolve(dirname(fileURLToPath(import.meta.url)), "../../src/ResolveAI.App");
const compiler = await compile(readFileSync(join(app, "Styles/app.css"), "utf8"), { base: join(app, "Styles"), onDependency: () => {} });
const scanner = new Scanner({ sources: compiler.sources });
const candidates = scanner.scan();
const { code } = optimize(compiler.build(candidates), { minify: true });
writeFileSync(join(app, "wwwroot/app.css"), code);
console.log(`wwwroot/app.css: ${candidates.length} candidates, ${code.length} bytes`);
