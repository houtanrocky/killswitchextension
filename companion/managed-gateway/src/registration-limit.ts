import { DurableObject } from "cloudflare:workers";

const WINDOW_MS = 60 * 60 * 1000;
const MAX_REGISTRATIONS_PER_WINDOW = 5;

export class RegistrationLimit extends DurableObject<Env> {
	constructor(ctx: DurableObjectState, env: Env) {
		super(ctx, env);
		ctx.blockConcurrencyWhile(async () => {
			ctx.storage.sql.exec(`
				CREATE TABLE IF NOT EXISTS registration_window (
					window_start INTEGER NOT NULL,
					count INTEGER NOT NULL
				)
			`);
		});
	}

	consume(now: number): boolean {
		const row = this.ctx.storage.sql.exec<{ window_start: number; count: number }>(
			"SELECT window_start, count FROM registration_window LIMIT 1"
		).toArray()[0];
		if (!row || now - row.window_start >= WINDOW_MS) {
			this.ctx.storage.sql.exec("DELETE FROM registration_window");
			this.ctx.storage.sql.exec("INSERT INTO registration_window (window_start, count) VALUES (?, 1)", now);
			return true;
		}
		if (row.count >= MAX_REGISTRATIONS_PER_WINDOW) return false;
		this.ctx.storage.sql.exec("UPDATE registration_window SET count = count + 1");
		return true;
	}
}
