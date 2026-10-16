typedef signed char i8;
typedef unsigned char u8;
typedef short i16;
typedef unsigned short u16;
typedef int i32;
typedef unsigned int u32;
typedef long long i64;
typedef unsigned long long u64;
typedef __int128 i128;

int printf(const char* format, ...);

i64 widths(i8 a, u8 b, i16 c, u16 d, i32 e, u32 f, i64 g, u64 h)
{
    printf("C widths %d %u %d %u %d %u %lld %llu\n", a, b, c, d, e, f, g, h);
    return (i64)a + b + c + d;
}

i64 widen_i8(i8 x) { return x; }
u64 widen_u8(u8 x) { return x; }
i64 widen_i16(i16 x) { return x; }
u64 widen_u16(u16 x) { return x; }

i8 negate_i8(i8 x) { return (i8)-x; }
u8 invert_u8(u8 x) { return (u8)~x; }
i16 negate_i16(i16 x) { return (i16)-x; }
u16 invert_u16(u16 x) { return (u16)~x; }

_Bool is_negative(i64 x) { return x < 0; }
i32 count_true(_Bool a, _Bool b, _Bool c) { return a + b + c; }

i64 many_ints(i32 a, i32 b, i32 c, i32 d, i32 e, i32 f, i32 g, i32 h, i8 i, i16 j)
{
    printf("C many_ints %d %d %d %d %d %d %d %d %d %d\n", a, b, c, d, e, f, g, h, i, j);
    return (i64)a + b + c + d + e + f + g + h + i + j;
}

double many_doubles(double a, double b, double c, double d, double e, double f, double g, double h, double i,
                    float j)
{
    printf("C many_doubles %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f %.2f\n", a, b, c, d, e, f, g, h, i, j);
    return a + b + c + d + e + f + g + h + i + j;
}

double interleaved(i32 a, double b, i8 c, float d, i64 e, double f, u16 g, float h)
{
    printf("C interleaved %d %.2f %d %.2f %lld %.2f %u %.2f\n", a, b, c, d, e, f, g, h);
    return a + b + c + d + (double)e + f + g + h;
}

float half(float x) { return x / 2; }

void show_i128(i128 x)
{
    printf("C i128 %016llx%016llx\n", (u64)((unsigned __int128)x >> 64), (u64)x);
}

i128 multiply_i128(i128 a, i64 b) { return a * b; }

i64 after_five(i64 a, i64 b, i64 c, i64 d, i64 e, i128 f, i64 g)
{
    printf("C after_five %lld %lld %lld %lld %lld %lld\n", a, b, c, d, e, g);
    show_i128(f);
    return g;
}
