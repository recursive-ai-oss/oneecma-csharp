const r = new FinalizationRegistry(() => {}); for (let i = 0; ; i++) r.register({}, i);
