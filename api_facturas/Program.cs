// ============================================================
// Program.cs — el PUNTO DE ENTRADA de la API (el "main" de .NET).
//
// Aquí se arma la aplicación: se registran los servicios (el
// ENSAMBLADOR de las capas), se configura cómo responder cuando
// una petición no valida (422), y se encienden las rutas.
//
// El recorrido completo de una petición está explicado en
// docs/FLUJO_DE_UNA_PETICION.md.
// ============================================================

// "using" trae tipos de otros espacios de nombres para poder usarlos:
using System.Text;
using ApiFacturas.Autorizacion;
using ApiFacturas.Modelos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ApiFacturas.Repositorios;
using ApiFacturas.Servicios;
using Microsoft.AspNetCore.Mvc;

// El "builder" es el constructor de la aplicación: a él se le
// registra TODO antes de arrancar.
var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// 1. EL ENSAMBLADOR — el único lugar que conoce clases concretas
// ------------------------------------------------------------
// Aquí se le dice al contenedor de dependencias de .NET qué clase
// concreta entregar cuando alguien pida una INTERFAZ:
//   - pide IRepositorioProducto → recibe RepositorioProductoPostgres
//   - pide IServicioProducto    → recibe ServicioProducto
// El controlador y el servicio JAMÁS hacen "new" de clases concretas:
// las reciben por constructor (inyección de dependencias).
// Cuando la v5 agregue otro motor, SOLO estas líneas cambiarán.

// La cadena de conexión: viene de appsettings.json, y en Docker la
// sobreescribe la variable de entorno ConnectionStrings__Postgres.
var cadenaConexion = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Falta la cadena de conexión 'Postgres'.");

// AddScoped = "una instancia por peticion HTTP" (cada request estrena la suya).
// Es el ensamblador, y crece de a una linea por recurso. Esa lista larga es
// deliberada: cuando llegue el SEGUNDO MOTOR -la v5- es el argumento de la
// fabrica, y el dolor de hoy es lo que la justifica.

// ------------------------------------------------------------
// LA v1 — las SEIS tablas SIN clave foranea
// ------------------------------------------------------------
// El criterio de la v1 es ese y no otro: ninguna de estas seis depende de
// otra fila para existir, asi que se pueden construir en cualquier orden.
builder.Services.AddScoped<IRepositorioProducto>(
    _ => new RepositorioProductoPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioProducto, ServicioProducto>();
builder.Services.AddScoped<IRepositorioEmpresa>(
    _ => new RepositorioEmpresaPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioEmpresa, ServicioEmpresa>();
builder.Services.AddScoped<IRepositorioPersona>(
    _ => new RepositorioPersonaPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioPersona, ServicioPersona>();
builder.Services.AddScoped<IRepositorioRol>(
    _ => new RepositorioRolPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioRol, ServicioRol>();
builder.Services.AddScoped<IRepositorioRuta>(
    _ => new RepositorioRutaPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioRuta, ServicioRuta>();
builder.Services.AddScoped<IRepositorioUsuario>(
    _ => new RepositorioUsuarioPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioUsuario, ServicioUsuario>();

// ------------------------------------------------------------
// LA v2 — las SEIS tablas CON clave foranea, y con ellas estan las 12
// ------------------------------------------------------------
// La v2 INCLUYE la v1: no se reinicia nada. Lo de arriba sigue en pie y esto
// se le suma.
//
// Aqui aparecen los procedimientos almacenados y el trigger: factura no se
// arma con INSERT sueltos, y usuario-con-roles tampoco.
builder.Services.AddScoped<IRepositorioCliente>(
    _ => new RepositorioClientePostgres(cadenaConexion));
builder.Services.AddScoped<IServicioCliente, ServicioCliente>();
builder.Services.AddScoped<IRepositorioVendedor>(
    _ => new RepositorioVendedorPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioVendedor, ServicioVendedor>();
builder.Services.AddScoped<IRepositorioFactura>(
    _ => new RepositorioFacturaPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioFactura, ServicioFactura>();
builder.Services.AddScoped<IRepositorioRolUsuario>(
    _ => new RepositorioRolUsuarioPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioRolUsuario, ServicioRolUsuario>();
builder.Services.AddScoped<IRepositorioRutaRol>(
    _ => new RepositorioRutaRolPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioRutaRol, ServicioRutaRol>();

// El recurso MAESTRO-DETALLE sobre la tabla puente: el usuario Y sus roles en
// una sola operacion (RF9). No es una tabla mas -son las mismas dos-, es otra
// forma de operarlas, y es la que usa la interfaz grafica.
builder.Services.AddScoped<IRepositorioUsuarioConRoles>(
    _ => new RepositorioUsuarioConRolesPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioUsuarioConRoles, ServicioUsuarioConRoles>();


// ------------------------------------------------------------
// 1bis. EL CONTROL DE ACCESO (v3)
// ------------------------------------------------------------
// Dos cosas distintas, y el orden en que se nombran no es casual:
//
//   AUTENTICACION  ¿quien es usted?   -> el token, y 401 si no hay
//   AUTORIZACION   ¿que puede hacer?  -> verificar_acceso_ruta, y 403 si no
//
// Se confunden porque en ingles las dos empiezan igual -de ahi que se escriban
// authn y authz-. Aqui estan separadas a proposito: lo de abajo resuelve la
// primera; el atributo [ExigePermiso] resuelve la segunda.

// La configuracion del token. Sale de appsettings.json, y en Docker la
// sobreescribe el compose con Jwt__Key, Jwt__Issuer...
var configuracionJwt = new ConfiguracionJwt();
builder.Configuration.GetSection("Jwt").Bind(configuracionJwt);
if (string.IsNullOrWhiteSpace(configuracionJwt.Key) || configuracionJwt.Key.Length < 32)
{
    // HMAC-SHA256 pide al menos 32 bytes de clave. Fallar aqui, al arrancar, es
    // mucho mejor que fallar al firmar el primer token -que seria un 500 que
    // nadie relaciona con la configuracion-.
    throw new InvalidOperationException(
        "La clave Jwt:Key falta o tiene menos de 32 caracteres.");
}
builder.Services.AddSingleton(configuracionJwt);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opciones =>
    {
        opciones.TokenValidationParameters = new TokenValidationParameters
        {
            // Las cuatro validaciones, y las cuatro importan:
            ValidateIssuer = true,            // que lo haya emitido ESTA API
            ValidateAudience = true,          // que sea para ESTA API
            ValidateLifetime = true,          // que no haya vencido
            ValidateIssuerSigningKey = true,  // QUE NADIE LO HAYA ALTERADO
            ValidIssuer = configuracionJwt.Issuer,
            ValidAudience = configuracionJwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuracionJwt.Key)),

            // Por defecto ASP.NET perdona 5 minutos de reloj desadaptado. Se
            // baja a cero para que «vencido» signifique vencido: si no, el
            // criterio del token expirado da 200 durante cinco minutos y
            // parece que el codigo esta mal.
            ClockSkew = TimeSpan.Zero,
        };

        // El 401 de la API, con el mismo sobre que todos los demas errores. Sin
        // esto, ASP.NET responde un 401 con el cuerpo VACIO y la interfaz
        // grafica no tiene nada que mostrarle a la persona.
        opciones.Events = new JwtBearerEvents
        {
            OnChallenge = async contexto =>
            {
                contexto.HandleResponse();
                contexto.Response.StatusCode = 401;
                contexto.Response.ContentType = "application/json";
                await contexto.Response.WriteAsync(
                    "{\"estado\":401,\"mensaje\":\"No hay una sesion valida. "
                    + "Inicie sesion.\"}");
            },
        };
    });

builder.Services.AddAuthorization();

// El repositorio del acceso: la unica puerta a verificar_acceso_ruta.
builder.Services.AddScoped<IRepositorioAcceso>(
    _ => new RepositorioAccesoPostgres(cadenaConexion));
builder.Services.AddScoped<IServicioSesion, ServicioSesion>();

// ------------------------------------------------------------
// 2. Los controladores y la validación de la petición (el 422)
// ------------------------------------------------------------
// AddControllers activa el sistema de controladores ([ApiController]).
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(opciones =>
    {
        // Cuando un body NO cumple las reglas de su petición (las anotaciones
        // [Required], [Range]... de Peticiones/), ASP.NET arma solo la
        // respuesta de error. Aquí la personalizamos para que sea un
        // 422 con la lista de errores — el formato del contrato:
        opciones.InvalidModelStateResponseFactory = contexto =>
        {
            // Recorrer el ModelState y sacar cada mensaje de error:
            var errores = new List<string>();
            foreach (var campo in contexto.ModelState)
            {
                foreach (var error in campo.Value.Errors)
                {
                    errores.Add(error.ErrorMessage);
                }
            }
            // ObjectResult = "responde este objeto como JSON, con este código":
            return new ObjectResult(new
            {
                estado = 422,
                mensaje = "Datos inválidos.",
                errores
            })
            { StatusCode = 422 };
        };
    });

// ------------------------------------------------------------
// 2b. Swagger — la documentación interactiva de la API
// ------------------------------------------------------------
// Swashbuckle lee los controladores y sus clases de datos y genera una página
// donde se ven TODOS los endpoints y se pueden probar desde el
// navegador (http://localhost:8044/swagger).
builder.Services.AddEndpointsApiExplorer();   // descubre los endpoints
builder.Services.AddSwaggerGen();             // arma el documento OpenAPI

// Construir la aplicación con todo lo registrado:
var app = builder.Build();

// Encender Swagger: el JSON (OpenAPI) y la página interactiva:
app.UseSwagger();
app.UseSwaggerUI();

// ------------------------------------------------------------
// 3. Las rutas
// ------------------------------------------------------------

// GET / — diagnóstico (usable como healthcheck). MapGet registra una
// ruta directa sin necesidad de un controlador:
app.MapGet("/", () => Results.Json(new
{
    mensaje = "API Facturas funcionando",
    version = "v3",
    contratos = "docs/spec_kit/versiones/v3_control_acceso/6_contracts.md"
}))
   // El diagnostico se queda ABIERTO, y es deliberado: sirve para
   // saber si la API esta viva, y para eso no hace falta identificarse.
   // Junto con POST /api/sesion son los DOS unicos endpoints sin token
   // de toda la API.
   .AllowAnonymous();

// MapControllers enciende las rutas declaradas con atributos en los
// controladores ([Route], [HttpGet], [HttpPost]...):
// ------------------------------------------------------------
// EL CONTROL DE ACCESO, Y EL ORDEN IMPORTA (v3)
// ------------------------------------------------------------
// UseAuthentication va ANTES de UseAuthorization, y no es un gusto: el primero
// lee el token y averigua QUIEN es; el segundo decide si PUEDE. Al reves, el
// segundo no tendria a quien consultar — y dejaria pasar todo.
//
// Los dos van antes de MapControllers, porque si los controladores se mapearan
// primero, la peticion llegaria al metodo sin que nadie hubiera preguntado
// nada.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Arrancar y quedarse escuchando (el puerto lo fija ASPNETCORE_URLS
// en el Dockerfile: 8044):
app.Run();
