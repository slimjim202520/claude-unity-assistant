export default async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  if (req.method === 'OPTIONS') return res.status(200).end();
  if (req.method !== 'POST') return res.status(405).json({ error: 'Method not allowed' });
  const { key } = req.body || {};
  if (!key) return res.status(400).json({ valid: false, tier: 'free', message: 'No key provided' });
  let validKeys = [];
  try { validKeys = JSON.parse(process.env.KV_KEYS || '[]'); } catch {}
  const cleanKey = key.trim().toUpperCase();
  const isValid = validKeys.includes(cleanKey);
  if (isValid) return res.status(200).json({ valid: true, tier: 'pro', message: 'Welcome to Claude Unity Assistant Pro!' });
  return res.status(200).json({ valid: false, tier: 'free', message: 'Get Pro at patreon.com/slimjim202520' });
}
