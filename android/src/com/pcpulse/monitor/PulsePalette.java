package com.pcpulse.monitor;

final class PulsePalette {
    final int start;
    final int background;
    final int end;
    final int field;
    final int panel;
    final int border;
    final int text;
    final int muted;
    final int accent;
    final int primary;
    final int memory;
    final int thermal;

    private PulsePalette(int start, int background, int end, int field, int panel, int border,
                         int text, int muted, int accent, int primary, int memory, int thermal) {
        this.start = start;
        this.background = background;
        this.end = end;
        this.field = field;
        this.panel = panel;
        this.border = border;
        this.text = text;
        this.muted = muted;
        this.accent = accent;
        this.primary = primary;
        this.memory = memory;
        this.thermal = thermal;
    }

    static PulsePalette forDay(boolean day) {
        return day
                ? new PulsePalette(0xffdfeefc, 0xffeff6ff, 0xfff6f8fd, 0xffe8f0fa, 0xfff8fbff, 0xffc8d6e6,
                        0xff19283a, 0xff46586e, 0xff075db6, 0xff075db6, 0xff086b73, 0xffa74317)
                : new PulsePalette(0xff25354f, 0xff161e2c, 0xff1b1d32, 0xff2a3950, 0xff253249, 0xff3b4f6a,
                        0xfff1f5fc, 0xffb8c5db, 0xff9bb8ff, 0xff1468b8, 0xffa0e4e3, 0xffffc28f);
    }
}
