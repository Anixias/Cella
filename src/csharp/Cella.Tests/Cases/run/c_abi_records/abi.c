typedef signed char i8;
typedef unsigned char u8;
typedef short i16;
typedef int i32;
typedef long long i64;
typedef unsigned long long u64;
typedef __int128 i128;

int printf(const char* format, ...);

struct Pair
{
    i32 a, b;
};

struct Floats
{
    float x, y;
};

struct Doubles
{
    double x, y;
};

struct Mixed
{
    i64 n;
    double d;
};

struct Rgb
{
    u8 r, g, b;
};

struct Triple
{
    i32 a, b, c;
};

struct Big
{
    i64 a, b, c;
};

struct Vec3
{
    float x, y, z;
};

struct Quad
{
    float v[4];
};

struct Padded
{
    i8 tag;
    i16 s;
    i32 n;
    float f;
};

struct FloatInt
{
    float f;
    i32 n;
};

struct Nested
{
    struct Pair p;
    float f;
};

struct Flagged
{
    _Bool flag;
    i32 n;
};

struct Wide
{
    i128 v;
};

struct Pair twice_pair(struct Pair v)
{
    printf("C pair %d %d\n", v.a, v.b);
    return (struct Pair){v.a * 2, v.b * 2};
}

struct Floats twice_floats(struct Floats v)
{
    printf("C floats %.2f %.2f\n", v.x, v.y);
    return (struct Floats){v.x * 2, v.y * 2};
}

struct Doubles twice_doubles(struct Doubles v)
{
    printf("C doubles %.3f %.3f\n", v.x, v.y);
    return (struct Doubles){v.x * 2, v.y * 2};
}

struct Mixed twice_mixed(struct Mixed v)
{
    printf("C mixed %lld %.2f\n", v.n, v.d);
    return (struct Mixed){v.n * 2, v.d * 2};
}

struct Rgb invert_rgb(struct Rgb v)
{
    printf("C rgb %u %u %u\n", v.r, v.g, v.b);
    return (struct Rgb){(u8)(255 - v.r), (u8)(255 - v.g), (u8)(255 - v.b)};
}

struct Triple twice_triple(struct Triple v)
{
    printf("C triple %d %d %d\n", v.a, v.b, v.c);
    return (struct Triple){v.a * 2, v.b * 2, v.c * 2};
}

struct Big twice_big(struct Big v)
{
    printf("C big %lld %lld %lld\n", v.a, v.b, v.c);
    return (struct Big){v.a * 2, v.b * 2, v.c * 2};
}

struct Vec3 twice_vec3(struct Vec3 v)
{
    printf("C vec3 %.2f %.2f %.2f\n", v.x, v.y, v.z);
    return (struct Vec3){v.x * 2, v.y * 2, v.z * 2};
}

struct Quad twice_quad(struct Quad v)
{
    printf("C quad %.2f %.2f %.2f %.2f\n", v.v[0], v.v[1], v.v[2], v.v[3]);
    return (struct Quad){{v.v[0] * 2, v.v[1] * 2, v.v[2] * 2, v.v[3] * 2}};
}

struct Padded twice_padded(struct Padded v)
{
    printf("C padded %d %d %d %.2f\n", v.tag, v.s, v.n, v.f);
    return (struct Padded){(i8)(v.tag * 2), (i16)(v.s * 2), v.n * 2, v.f * 2};
}

struct FloatInt twice_float_int(struct FloatInt v)
{
    printf("C float_int %.2f %d\n", v.f, v.n);
    return (struct FloatInt){v.f * 2, v.n * 2};
}

struct Nested twice_nested(struct Nested v)
{
    printf("C nested %d %d %.2f\n", v.p.a, v.p.b, v.f);
    return (struct Nested){{v.p.a * 2, v.p.b * 2}, v.f * 2};
}

struct Flagged flip_flagged(struct Flagged v)
{
    printf("C flagged %d %d\n", v.flag, v.n);
    return (struct Flagged){!v.flag, v.n + 1};
}

void show_wide(struct Wide v)
{
    printf("C wide %016llx%016llx\n", (u64)((unsigned __int128)v.v >> 64), (u64)v.v);
}

struct Wide twice_wide(struct Wide v)
{
    show_wide(v);
    return (struct Wide){v.v * 2};
}

struct Doubles late_doubles(double a, double b, double c, double d, double e, double f, double g, struct Doubles v,
                            double after)
{
    printf("C late_doubles %.1f %.1f %.1f %.1f %.1f %.1f %.1f %.1f %.1f %.1f\n", a, b, c, d, e, f, g, v.x, v.y, after);
    return (struct Doubles){v.y, after};
}

i64 late_triple(i64 a, i64 b, i64 c, i64 d, i64 e, struct Triple v, i64 after)
{
    printf("C late_triple %lld %lld %lld %lld %lld %d %d %d %lld\n", a, b, c, d, e, v.a, v.b, v.c, after);
    return a + b + c + d + e + v.a + v.b + v.c + after;
}

i32 consume_triple(struct Triple v)
{
    v.a = 999;
    return v.a + v.b + v.c;
}

i64 consume_big(struct Big v)
{
    v.a = -1;
    return v.a + v.b + v.c;
}

void scale_pair(struct Pair* v, i32 factor)
{
    v->a *= factor;
    v->b *= factor;
}

struct Big make_big(i64 first)
{
    return (struct Big){first, first + 1, first + 2};
}
