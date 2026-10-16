// Test-only stand-in used to check the "game is running" guard. It is NOT the game and does nothing but wait;
// it exits by itself after at most two minutes so a failed test cannot leave it running.
Thread.Sleep(TimeSpan.FromSeconds(120));
