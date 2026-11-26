import { DevicePolicy, type GatewayRule } from "./device-policy";
import { RegistrationLimit } from "./registration-limit";

export { DevicePolicy, RegistrationLimit };

interface FetchInput {
	url: string;
	method: string;
	headers?: Record<string, string[]>;
	body?: string;
}

const MAX_POLICY_BYTES = 4 * 1024 * 1024;
const MAX_FETCH_BYTES = 24 * 1024 * 1024;
const MAX_RULES = 512;
const HOP_BY_HOP_HEADERS = new Set([
	"connection", "proxy-connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
	"te", "trailer", "transfer-encoding", "upgrade", "host", "content-length",
	"cf-connecting-ip", "cf-connecting-ipv6", "true-client-ip", "x-real-ip", "x-forwarded-for", "forwarded",
]);

export default {
	async fetch(request: Request, env: Env): Promise<Response> {
		const path = new URL(request.url).pathname;
		try {
			if (path === "/v1/register" && request.method === "POST") return await registerDevice(request, env);
			if (path === "/v1/policy" && request.method === "PUT") return await updatePolicy(request, env);
			if (path === "/v1/ip" && request.method === "GET") return await getClientIp(request, env);
			if (path === "/v1/fetch" && request.method === "POST") return await fetchTarget(request, env);
			return new Response("Not found", { status: 404 });
		} catch (error) {
			console.error(JSON.stringify({ message: "gateway request failed", path, error: error instanceof Error ? error.message : "unknown" }));
			return json({ error: "Gateway request failed" }, 502);
		}
	},
} satisfies ExportedHandler<Env>;

async function registerDevice(request: Request, env: Env): Promise<Response> {
	const ip = clientIpv4(request);
	if (!ip) return json({ error: "A public IPv4 address is required" }, 503);
	const ipHash = await sha256Hex(ip);
	const limiter = env.REGISTRATION_LIMITS.getByName(ipHash);
	if (!await limiter.consume(Date.now())) return json({ error: "Too many devices registered from this network. Try again later." }, 429);

	const bytes = new Uint8Array(32);
	crypto.getRandomValues(bytes);
	const token = toHex(bytes);
	const tokenHash = await sha256Hex(token);
	await env.DEVICE_POLICIES.getByName(tokenHash).register(Date.now());
	return json({ token }, 201);
}

async function updatePolicy(request: Request, env: Env): Promise<Response> {
	const policy = await authorizedPolicy(request, env);
	if (!policy) return json({ error: "Unauthorized" }, 401);
	const parsed = await readJson(request, MAX_POLICY_BYTES);
	if (parsed instanceof Response) return parsed;
	if (!Array.isArray(parsed) || parsed.length > MAX_RULES) return json({ error: "Invalid policy" }, 400);
	const rules: GatewayRule[] = [];
	for (const value of parsed) {
		if (!isRecord(value) || typeof value.target !== "string" || typeof value.ipConditions !== "string")
			return json({ error: "Invalid policy rule" }, 400);
		if (!value.target.trim() || value.target.length > 2048 || !validIpConditions(value.ipConditions))
			return json({ error: "Invalid policy rule" }, 400);
		rules.push({ target: value.target.trim(), ipConditions: value.ipConditions });
	}
	await policy.stub.setPolicy(rules);
	return new Response(null, { status: 204 });
}

async function getClientIp(request: Request, env: Env): Promise<Response> {
	const policy = await authorizedPolicy(request, env);
	if (!policy) return json({ error: "Unauthorized" }, 401);
	const ip = clientIpv4(request);
	if (!ip) return json({ error: "Client IPv4 address unavailable" }, 503);
	return new Response(ip, { headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" } });
}

async function fetchTarget(request: Request, env: Env): Promise<Response> {
	const policy = await authorizedPolicy(request, env);
	if (!policy) return json({ error: "Unauthorized" }, 401);
	const parsed = await readJson(request, MAX_FETCH_BYTES);
	if (parsed instanceof Response) return parsed;
	if (!isRecord(parsed) || typeof parsed.url !== "string" || typeof parsed.method !== "string")
		return json({ error: "Invalid fetch request" }, 400);
	let target: URL;
	try { target = new URL(parsed.url); } catch { return json({ error: "Invalid target URL" }, 400); }
	if (!(["http:", "https:"].includes(target.protocol)) || !target.hostname || target.username || target.password || isPrivateHost(target.hostname))
		return json({ error: "Target URL is not allowed" }, 400);
	const method = parsed.method.toUpperCase();
	if (!/^[A-Z][A-Z0-9!#$%&'*+.^_`|~-]{0,31}$/.test(method) || method === "CONNECT" || method === "TRACE")
		return json({ error: "HTTP method is not allowed" }, 400);
	const headers = makeHeaders(parsed.headers);
	const rules = policy.rules;
	const matchingRules = rules.filter((rule) => ruleMatches(rule.target, target));
	if (matchingRules.length > 0) {
		const clientIp = clientIpv4(request);
		if (!clientIp || matchingRules.some((rule) => shouldBlock(clientIp, rule.ipConditions)))
			return new Response("Access limited by Kill Switch", { status: 451, headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" } });
	}
	let body: Uint8Array | undefined;
	if (typeof parsed.body === "string" && parsed.body.length > 0) {
		try { body = fromBase64(parsed.body); } catch { return json({ error: "Invalid request body" }, 400); }
	}
	if (method === "GET" || method === "HEAD") body = undefined;
	let requestBody: ArrayBuffer | undefined;
	if (body) {
		requestBody = new ArrayBuffer(body.byteLength);
		new Uint8Array(requestBody).set(body);
	}
	const upstream = await fetch(target, { method, headers, body: requestBody, redirect: "manual" });
	const responseHeaders = new Headers();
	upstream.headers.forEach((value, name) => {
		if (!HOP_BY_HOP_HEADERS.has(name.toLowerCase())) responseHeaders.append(name, value);
	});
	return new Response(upstream.body, { status: upstream.status, statusText: upstream.statusText, headers: responseHeaders });
}

async function authorizedPolicy(request: Request, env: Env): Promise<{ stub: DurableObjectStub<DevicePolicy>; rules: GatewayRule[] } | null> {
	const match = /^Bearer ([0-9a-f]{64})$/i.exec(request.headers.get("Authorization") ?? "");
	if (!match) return null;
	const hash = await sha256Hex(match[1]!.toLowerCase());
	const stub = env.DEVICE_POLICIES.getByName(hash);
	const rules = await stub.getPolicy();
	return rules === null ? null : { stub, rules };
}

async function readJson(request: Request, maxBytes: number): Promise<unknown | Response> {
	const length = Number(request.headers.get("Content-Length") ?? 0);
	if (length > maxBytes) return json({ error: "Request body is too large" }, 413);
	if (!request.body) return json({ error: "Request body is required" }, 400);
	const reader = request.body.getReader();
	const chunks: Uint8Array[] = [];
	let size = 0;
	while (true) {
		const { done, value } = await reader.read();
		if (done) break;
		size += value.byteLength;
		if (size > maxBytes) {
			await reader.cancel();
			return json({ error: "Request body is too large" }, 413);
		}
		chunks.push(value);
	}
	const combined = new Uint8Array(size);
	let offset = 0;
	for (const chunk of chunks) { combined.set(chunk, offset); offset += chunk.byteLength; }
	try { return JSON.parse(new TextDecoder().decode(combined)) as unknown; }
	catch { return json({ error: "Invalid JSON" }, 400); }
}

function clientIpv4(request: Request): string | null {
	const value = request.headers.get("CF-Connecting-IP")?.trim();
	return value && isIpv4(value) ? value : null;
}

function isIpv4(value: string): boolean {
	const parts = value.split(".");
	return parts.length === 4 && parts.every((part) => /^(0|[1-9]\d{0,2})$/.test(part) && Number(part) <= 255);
}

function validIpConditions(raw: string): boolean {
	const values = raw.replaceAll("|", "").split(",").map((value) => value.trim()).filter(Boolean);
	return values.length > 0 && values.length <= 64 && values.every((value) => isIpv4(value.startsWith("!") ? value.slice(1) : value));
}

function shouldBlock(clientIp: string, raw: string): boolean {
	const values = raw.replaceAll("|", "").split(",").map((value) => value.trim()).filter(Boolean);
	if (!isIpv4(clientIp) || values.length === 0) return true;
	const denied = values.some((value) => value.startsWith("!") && value.slice(1) === clientIp);
	const allowed = values.filter((value) => !value.startsWith("!"));
	return denied || (allowed.length > 0 && !allowed.includes(clientIp));
}

function ruleMatches(rawTarget: string, url: URL): boolean {
	let pattern = rawTarget.trim().toLowerCase().replace(/^https?:\/\//, "");
	const host = url.hostname.toLowerCase();
	const candidate = (url.host + url.pathname + url.search).toLowerCase();
	if (!pattern) return false;
	if (pattern.startsWith("*.") && !/[/*?#]/.test(pattern.slice(2))) {
		const suffix = pattern.slice(2);
		return host === suffix || host.endsWith(`.${suffix}`);
	}
	if (pattern === host && !/[/*?#]/.test(pattern)) return true;
	if (pattern.includes("*")) {
		const escaped = pattern.split("*").map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join(".*");
		return new RegExp(`^${escaped}$`, "i").test(candidate);
	}
	return candidate === pattern || candidate.startsWith(`${pattern}/`) || candidate.startsWith(`${pattern}?`);
}

function isPrivateHost(hostname: string): boolean {
	const host = hostname.toLowerCase().replace(/^\[|\]$/g, "");
	if (host === "localhost" || host.endsWith(".localhost") || host.endsWith(".local") || host.endsWith(".internal")) return true;
	if (!isIpv4(host)) return false;
	const [a, b] = host.split(".").map(Number);
	return a === 0 || a === 10 || a === 127 || (a === 169 && b === 254) || (a === 172 && b! >= 16 && b! <= 31) || (a === 192 && b === 168) || a! >= 224;
}

function makeHeaders(input: unknown): Headers {
	const headers = new Headers();
	if (!isRecord(input)) return headers;
	for (const [name, values] of Object.entries(input)) {
		if (HOP_BY_HOP_HEADERS.has(name.toLowerCase()) || /[\r\n]/.test(name) || !Array.isArray(values)) continue;
		for (const value of values) if (typeof value === "string" && !/[\r\n]/.test(value)) headers.append(name, value);
	}
	return headers;
}

function fromBase64(value: string): Uint8Array {
	const binary = atob(value);
	return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

function toHex(bytes: Uint8Array): string {
	return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function sha256Hex(value: string): Promise<string> {
	return toHex(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value))));
}

function isRecord(value: unknown): value is Record<string, unknown> {
	return typeof value === "object" && value !== null && !Array.isArray(value);
}

function json(value: unknown, status = 200): Response {
	return Response.json(value, { status, headers: { "Cache-Control": "no-store" } });
}
