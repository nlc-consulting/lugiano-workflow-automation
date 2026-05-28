import 'dotenv/config';
import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import { DocumentBuilder, SwaggerModule } from '@nestjs/swagger';
import { AppModule } from './app.module';

async function bootstrap() {
  const app = await NestFactory.create(AppModule);

  // exposedHeaders lets react-admin's data provider read the pagination total.
  app.enableCors({ exposedHeaders: ['Content-Range'] });

  // Match biostar: serve under /api in dev so the UI proxy hits /api/*.
  if (process.env.NODE_ENV !== 'production') {
    app.setGlobalPrefix('api');
  }

  app.useGlobalPipes(
    new ValidationPipe({
      transform: true,
      whitelist: true,
      forbidNonWhitelisted: false,
    }),
  );

  const config = new DocumentBuilder()
    .setTitle('Lugiano Portal API')
    .setDescription('Portal API for the Lugiano workflow automation system')
    .setVersion('0.1')
    .addBearerAuth()
    .build();
  SwaggerModule.setup('api', app, SwaggerModule.createDocument(app, config));

  await app.listen(process.env.PORT ?? 3000);
}
void bootstrap();
