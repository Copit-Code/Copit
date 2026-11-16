/** Token-Verbrauch und Umrechnung in USD (Listenpreise pro 1 Mio. Tokens, einstellbar). */

export interface Usage { input: number; output: number; cacheRead: number; cacheWrite: number }

export interface Price { input: number; output: number }

/** Standardpreise je Modellfamilie (USD / 1M Tokens). Über copit.prices überschreibbar. */
export const DEFAULT_PRICES: Record<string, Price> = {
  opus: { input: 5, output: 25 },
  fable: { input: 5, output: 25 },
  sonnet: { input: 3, output: 15 },
  haiku: { input: 1, output: 5 },
  default: { input: 3, output: 15 },
};

export const emptyUsage = (): Usage => ({ input: 0, output: 0, cacheRead: 0, cacheWrite: 0 });

export function addUsage(a: Usage, b: Partial<Usage>): Usage {
  a.input += b.input ?? 0; a.output += b.output ?? 0; a.cacheRead += b.cacheRead ?? 0; a.cacheWrite += b.cacheWrite ?? 0;
  return a;
}

export const totalTokens = (u: Usage) => u.input + u.output + u.cacheRead + u.cacheWrite;

export function priceFor(model: string | undefined, table: Record<string, Price> = DEFAULT_PRICES): Price {
  const m = (model ?? "").toLowerCase();
  for (const key of Object.keys(table)) if (key !== "default" && m.includes(key)) return table[key];
  return table.default ?? DEFAULT_PRICES.default;
}

/** Kosten: Cache-Lesen 10 %, Cache-Schreiben 125 % des Input-Preises. */
export function costOf(u: Usage, model: string | undefined, table?: Record<string, Price>): number {
  const p = priceFor(model, table);
  return (u.input * p.input + u.output * p.output + u.cacheRead * p.input * 0.1 + u.cacheWrite * p.input * 1.25) / 1_000_000;
}

/** Usage-Objekt der Anthropic-API → Usage */
export function fromApiUsage(u: any): Usage {
  return {
    input: Number(u?.input_tokens ?? 0), output: Number(u?.output_tokens ?? 0),
    cacheRead: Number(u?.cache_read_input_tokens ?? 0), cacheWrite: Number(u?.cache_creation_input_tokens ?? 0),
  };
}
