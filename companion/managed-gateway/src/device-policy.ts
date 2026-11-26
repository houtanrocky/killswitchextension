import { DurableObject } from "cloudflare:workers";

export interface GatewayRule {
	target: string;
	ipConditions: string;
}

export class DevicePolicy extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.blockConcurrencyWhile(async () => {
			ctx.storage.sql.exec(`
				CREATE TABLE IF NOT EXISTS device (
					id INTEGER PRIMARY KEY CHECK (id = 1),
					registered INTEGER NOT NULL,
					policy TEXT NOT NULL,
					created_at INTEGER NOT NULL
				)
			`);
		});
	}

	register(now: number): void {
		this.ctx.storage.sql.exec(
			"INSERT OR IGNORE INTO device (id, registered, policy, created_at) VALUES (1, 1, '[]', ?)",
			now
		);
	}

	isRegistered(): boolean {
		return this.ctx.storage.sql.exec<{ registered: number }>("SELECT registered FROM device LIMIT 1").toArray()[0]?.registered === 1;
	}

	getPolicy(): GatewayRule[] | null {
		const row = this.ctx.storage.sql.exec<{ policy: string }>("SELECT policy FROM device WHERE registered = 1 LIMIT 1").toArray()[0];
		if (!row) return null;
		return JSON.parse(row.policy) as GatewayRule[];
	}

	setPolicy(rules: GatewayRule[]): void {
		this.ctx.storage.sql.exec("UPDATE device SET policy = ? WHERE registered = 1", JSON.stringify(rules));
	}
}
