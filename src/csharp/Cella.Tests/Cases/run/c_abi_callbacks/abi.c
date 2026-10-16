typedef signed char i8;
typedef unsigned char u8;
typedef unsigned short u16;
typedef int i32;
typedef long long i64;
typedef unsigned long long u64;
typedef __int128 i128;

int printf(const char* format, ...);

struct Pair
{
    i32 a, b;
};

struct Doubles
{
    double x, y;
};

struct Big
{
    i64 a, b, c;
};

struct Rgb
{
    u8 r, g, b;
};

struct Vec3
{
    float x, y, z;
};

struct Mixed
{
    i64 n;
    double d;
};

struct Pair call_pair(struct Pair (*f)(struct Pair, i32))
{
    struct Pair result = f((struct Pair){7, -8}, 3);
    printf("C got %d %d\n", result.a, result.b);
    return result;
}

double call_doubles(double (*f)(struct Doubles, float, i8))
{
    double result = f((struct Doubles){1.5, -2.5}, 0.25f, -7);
    printf("C got %.2f\n", result);
    return result;
}

struct Big call_big(struct Big (*f)(struct Big, struct Rgb))
{
    struct Big result = f((struct Big){1000000000000, 2, 3}, (struct Rgb){1, 2, 3});
    printf("C got %lld %lld %lld\n", result.a, result.b, result.c);
    return result;
}

i8 call_narrow(i8 (*f)(i8, u16, _Bool))
{
    i8 result = f(-5, 60000, 1);
    printf("C got %d\n", result);
    return result;
}

double call_many(double (*f)(i32, i32, i32, i32, i32, i32, i32, i32, double, double, double, double, double, double,
                             double, double, double))
{
    double result = f(1, 2, 3, 4, 5, 6, 7, 8, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5);
    printf("C got %.2f\n", result);
    return result;
}

struct Vec3 call_vec3(struct Vec3 (*f)(struct Vec3))
{
    struct Vec3 result = f((struct Vec3){1.0f, -2.0f, 0.5f});
    printf("C got %.2f %.2f %.2f\n", result.x, result.y, result.z);
    return result;
}

void call_wide(i128 (*f)(i128))
{
    i128 result = f(((i128)1 << 70) | 9);
    printf("C got wide %016llx%016llx\n", (u64)((unsigned __int128)result >> 64), (u64)result);
}

double cella_mix(struct Mixed v, i32 factor);

double call_named(void)
{
    double result = cella_mix((struct Mixed){-3, 0.5}, 4);
    printf("C got %.2f\n", result);
    return result;
}
